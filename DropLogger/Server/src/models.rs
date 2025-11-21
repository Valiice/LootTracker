use askama::Template;
use sea_orm::FromQueryResult;
use serde::{Deserialize, Serialize};

#[derive(Template)]
#[template(path = "stats.html")]
pub struct StatsTemplate {
    pub stats: Vec<DropRateDisplay>,
}

#[derive(Debug, FromQueryResult, Serialize)]
pub struct DropRateDisplay {
    pub mob_name: String,
    pub item_name: String,
    pub total_drops: i64,
    pub total_kills: i64,
    pub drop_rate: f64,
}

#[derive(Debug, Deserialize, Clone)]
pub struct DropSubmission {
    #[serde(rename = "ZoneID")]
    pub zone_id: i32,
    #[serde(rename = "ItemName")]
    pub item_name: String,
    #[serde(rename = "Quantity")]
    pub quantity: i32,
    #[serde(rename = "IsHQ")]
    pub is_hq: bool,
    #[serde(rename = "UserHash")]
    pub user_hash: String,
    #[serde(rename = "SourceMob")]
    pub source_mob: Option<String>,
    #[serde(rename = "ItemID")]
    pub item_id: i32, 
    #[serde(rename = "SourceMobID")]
    pub source_mob_id: Option<i32>,
}