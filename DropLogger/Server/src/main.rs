use axum::{
    routing::{get, post},
    Router,
};
use std::{sync::Arc, time::Duration};
use tracing::{info, error};
use regex::Regex;
use sea_orm::{
    Database, ConnectOptions, ConnectionTrait, Statement, DatabaseBackend
};

mod entities;
mod state;
mod models;
mod handlers;

use state::AppState;

#[tokio::main]
async fn main() -> anyhow::Result<()> {
    dotenvy::dotenv().ok();
    tracing_subscriber::fmt::init();

    info!("Starting Enterprise Drop Tracker...");

    let db_url = std::env::var("DATABASE_URL").expect("DATABASE_URL must be set");
    let redis_url = std::env::var("REDIS_URL").expect("REDIS_URL must be set");

    let mut opt = ConnectOptions::new(db_url);
    opt.max_connections(50)
       .min_connections(5)
       .connect_timeout(Duration::from_secs(8))
       .acquire_timeout(Duration::from_secs(8))
       .idle_timeout(Duration::from_secs(8))
       .sqlx_logging(false); 

    let db = Database::connect(opt).await.expect("Failed to connect to DB");

    let db_clone = db.clone();
    tokio::spawn(async move {
        let mut interval = tokio::time::interval(Duration::from_secs(3600)); 
        loop {
            interval.tick().await;
            info!("Refreshing Materialized View stats...");
            let res = db_clone.execute(Statement::from_string(
                DatabaseBackend::Postgres,
                "REFRESH MATERIALIZED VIEW drop_stats;"
            )).await;
            
            if let Err(e) = res {
                error!("Failed to refresh stats: {}", e);
            }
        }
    });

    let redis_client = redis::Client::open(redis_url).expect("Invalid Redis URL");

    let state = Arc::new(AppState {
        db,
        redis: redis_client,
        sanitizer: Regex::new(r"[^\w\s\-\']").unwrap(),
    });

    let app = Router::new()
        .route("/", get(handlers::show_dashboard))
        .route("/api/v1/submit", post(handlers::submit_drops))
        .route("/api/v1/stats", get(handlers::get_api_stats))
        .with_state(state);

    let listener = tokio::net::TcpListener::bind("0.0.0.0:3000").await?;
    info!("Server listening on port 3000");
    axum::serve(listener, app).await?;

    Ok(())
}