use axum::{
    routing::{get, post},
    Router,
};
use std::{sync::Arc, time::Duration, process::Command, path::Path};
use tracing::{info, error, warn};
use regex::Regex;
use sea_orm::{
    Database, ConnectOptions, ConnectionTrait, Statement, DatabaseBackend, DatabaseConnection
};

mod entities;
mod state;
mod models;
mod handlers;

use state::AppState;

fn is_docker_engine_running() -> bool {
    Command::new("docker")
        .arg("info")
        .stdout(std::process::Stdio::null())
        .stderr(std::process::Stdio::null())
        .status()
        .map(|s| s.success())
        .unwrap_or(false)
}

async fn start_docker_containers() {
    info!("Checking Docker status...");

    if !is_docker_engine_running() {
        warn!("Docker Engine is NOT running.");

        #[cfg(target_os = "windows")]
        {
            let docker_path = r"C:\Program Files\Docker\Docker\Docker Desktop.exe";
            if Path::new(docker_path).exists() {
                info!("Found Docker Desktop. Attempting to launch...");
                match Command::new(docker_path).spawn() {
                    Ok(_) => {
                        info!("Docker Desktop launching... waiting for engine to initialize.");
                        for i in 1..=12 {
                            tokio::time::sleep(Duration::from_secs(5)).await;
                            if is_docker_engine_running() {
                                info!("Docker Engine started successfully!");
                                break;
                            }
                            info!("Waiting for Docker Engine... (Attempt {}/12)", i);
                        }
                    },
                    Err(e) => error!("Failed to launch Docker Desktop: {}", e),
                }
            } else {
                error!("Could not find Docker Desktop at '{}'. Please start it manually.", docker_path);
            }
        }
    }

    info!("Starting containers...");
    match Command::new("docker")
        .args(["compose", "up", "-d"])
        .current_dir(".") 
        .output() 
    {
        Ok(output) => {
            let stderr = String::from_utf8_lossy(&output.stderr);
            if !stderr.is_empty() {
                if !stderr.contains("version is obsolete") {
                    warn!("Docker Output: {}", stderr);
                }
            }

            if output.status.success() {
                info!("Docker containers are up.");
            } else {
                warn!("Failed to start containers. Check if Docker is fully loaded.");
            }
        },
        Err(e) => {
            warn!("Failed to execute 'docker' command: {}. Is Docker installed?", e);
        }
    }
}

async fn connect_to_database_with_retry(opt: ConnectOptions) -> anyhow::Result<DatabaseConnection> {
    let max_retries = 20; // Increased retries since we might be waiting for Docker boot + DB boot
    let retry_delay = Duration::from_secs(2);

    for attempt in 1..=max_retries {
        match Database::connect(opt.clone()).await {
            Ok(conn) => return Ok(conn),
            Err(e) => {
                if attempt == max_retries {
                    error!("Max retries reached. Could not connect to database.");
                    return Err(e.into());
                }
                warn!("Database not ready yet (Attempt {}/{})... waiting 2s", attempt, max_retries);
                tokio::time::sleep(retry_delay).await;
            }
        }
    }
    unreachable!()
}

#[tokio::main]
async fn main() -> anyhow::Result<()> {
    dotenvy::dotenv().ok();
    tracing_subscriber::fmt::init();

    info!("Starting Enterprise Drop Tracker...");

    start_docker_containers().await;

    let db_url = std::env::var("DATABASE_URL").expect("DATABASE_URL must be set");
    let redis_url = std::env::var("REDIS_URL").expect("REDIS_URL must be set");

    let mut opt = ConnectOptions::new(db_url);
    opt.max_connections(50)
       .min_connections(5)
       .connect_timeout(Duration::from_secs(8))
       .acquire_timeout(Duration::from_secs(8))
       .idle_timeout(Duration::from_secs(8))
       .sqlx_logging(false); 

    let db = connect_to_database_with_retry(opt).await?;
    info!("Connected to Database successfully!");

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