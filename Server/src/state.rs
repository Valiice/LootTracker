use regex::Regex;
use sea_orm::DatabaseConnection;

pub struct AppState {
    pub db: DatabaseConnection,
    pub redis: redis::Client,
    pub sanitizer: Regex,
}