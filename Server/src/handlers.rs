use axum::{
    extract::{State, Json},
    http::{StatusCode, HeaderMap},
    response::{IntoResponse, Response, Html},
};
use std::sync::Arc;
use tracing::{info, warn, error};
use redis::AsyncCommands;
use sea_orm::{
    EntityTrait, Set, ActiveValue::NotSet, Statement, DatabaseBackend, FromQueryResult
};
use askama::Template;
use chrono::Utc;

use crate::state::AppState;
use crate::models::{DropSubmission, DropRateDisplay, StatsTemplate};
use crate::entities::{
    drops::Entity as DropEntity,
    drops::ActiveModel as DropActiveModel,
    stats::Entity as StatsEntity,
};

const MAX_REQUESTS_PER_USER: i32 = 100; 
const RATE_LIMIT_WINDOW_SEC: u64 = 60;

pub async fn show_dashboard(State(state): State<Arc<AppState>>) -> Response {
    let sql = "
        WITH kill_counts AS (
            SELECT 
                item_id AS mob_id, 
                SUM(quantity) as total_kills
            FROM drops 
            WHERE item_name LIKE 'MOB-%' 
            GROUP BY item_id
        )
        SELECT 
            MAX(d.source_mob) AS mob_name,
            MAX(d.item_name) AS item_name,
            SUM(d.quantity)::bigint AS total_drops,
            COALESCE(k.total_kills, 0)::bigint AS total_kills,
            CASE 
                WHEN k.total_kills > 0 THEN 
                    ROUND((CAST(SUM(d.quantity) AS NUMERIC) / k.total_kills) * 100, 2)::float8
                ELSE 0 
            END AS drop_rate
        FROM 
            drops d
        LEFT JOIN 
            kill_counts k ON k.mob_id = d.source_mob_id
        WHERE 
            d.source_mob IS NOT NULL
        GROUP BY 
            d.source_mob_id, d.item_id, k.total_kills
        ORDER BY 
            mob_name ASC,
            item_name ASC;
    ";

    let results = DropRateDisplay::find_by_statement(Statement::from_string(
        DatabaseBackend::Postgres, 
        sql.to_owned()
    ))
    .all(&state.db)
    .await;

    match results {
        Ok(data) => {
            let template = StatsTemplate { stats: data };
            match template.render() {
                Ok(html) => Html(html).into_response(),
                Err(e) => {
                    error!("Template rendering failed: {}", e);
                    (StatusCode::INTERNAL_SERVER_ERROR, "Template Error").into_response()
                }
            }
        },
        Err(e) => {
            error!("Dashboard Query Error: {}", e);
            (StatusCode::INTERNAL_SERVER_ERROR, "Database Error").into_response()
        }
    }
}

pub async fn submit_drops(
    State(state): State<Arc<AppState>>,
    headers: HeaderMap,
    Json(payload): Json<Vec<DropSubmission>>,
) -> Response { 
    let user_agent = headers
        .get("user-agent")
        .and_then(|v| v.to_str().ok())
        .unwrap_or("");

    if !user_agent.contains("DropLogger-Plugin") {
        return (StatusCode::FORBIDDEN, serde_json::json!({"error": "Invalid Client"}).to_string()).into_response();
    }

    if payload.is_empty() {
        return (StatusCode::BAD_REQUEST, serde_json::json!({"error": "Empty payload"}).to_string()).into_response();
    }

    let user_hash = &payload[0].user_hash;
    let rate_key = format!("rate_limit:{}", user_hash);

    let mut redis_conn = match state.redis.get_multiplexed_async_connection().await {
        Ok(conn) => conn,
        Err(e) => {
            error!("Redis error: {}", e);
            return (StatusCode::INTERNAL_SERVER_ERROR, "Internal error").into_response();
        }
    };

    let count: i32 = match redis_conn.incr(&rate_key, 1).await {
        Ok(val) => val,
        Err(_) => return (StatusCode::INTERNAL_SERVER_ERROR, "Redis error").into_response(),
    };

    if count == 1 {
        let _ = redis_conn.expire::<_, ()>(&rate_key, RATE_LIMIT_WINDOW_SEC as i64).await;
    }

    if count > MAX_REQUESTS_PER_USER {
        warn!("Rate limit exceeded for {}", user_hash);
        return (StatusCode::TOO_MANY_REQUESTS, "Too many requests").into_response();
    }

    let drop_entities: Vec<DropActiveModel> = payload.iter()
        .filter(|d| d.quantity > 0 && d.quantity < 100)
        .map(|d| {
            let safe_name = state.sanitizer.replace_all(&d.item_name, "");
            let safe_mob = d.source_mob.as_ref().map(|s| state.sanitizer.replace_all(s, "").to_string());
            
            DropActiveModel {
                id: NotSet,
                zone_id: Set(d.zone_id),
                item_name: Set(safe_name.to_string()),
                quantity: Set(d.quantity),
                is_hq: Set(d.is_hq),
                reporter_hash: Set(d.user_hash.clone()),
                created_at: Set(Utc::now().into()),
                source_mob: Set(safe_mob),
                item_id: Set(d.item_id),
                source_mob_id: Set(d.source_mob_id),
            }
        })
        .collect();

    if drop_entities.is_empty() {
        return (StatusCode::OK, serde_json::json!({"status": "No valid drops"}).to_string()).into_response();
    }

    match DropEntity::insert_many(drop_entities).exec(&state.db).await {
        Ok(_) => {
            info!("Batch inserted {} items for {}", payload.len(), user_hash);
            (StatusCode::OK, serde_json::json!({
                "success": true, 
                "count": payload.len() 
            }).to_string()).into_response()
        }
        Err(e) => {
            error!("DB Insert Error: {}", e);
            (StatusCode::INTERNAL_SERVER_ERROR, "Database error").into_response()
        }
    }
}

pub async fn get_api_stats(State(state): State<Arc<AppState>>) -> Response { 
    use sea_orm::EntityTrait; 
    let stats_result = StatsEntity::find().all(&state.db).await;
    match stats_result {
        Ok(stats) => (StatusCode::OK, Json(stats)).into_response(),
        Err(e) => (StatusCode::INTERNAL_SERVER_ERROR, Json(serde_json::json!({"error": e.to_string()}))).into_response()
    }
}