pub mod drops {
    use sea_orm::entity::prelude::*;

    #[derive(Clone, Debug, PartialEq, DeriveEntityModel)]
    #[sea_orm(table_name = "drops")]
    pub struct Model {
        #[sea_orm(primary_key)]
        pub id: i64,
        pub zone_id: i32,
        pub item_name: String,
        pub quantity: i32,
        pub is_hq: bool,
        pub reporter_hash: String,
        pub created_at: DateTimeWithTimeZone,
        pub source_mob: Option<String>,
        pub item_id: i32, 
        pub source_mob_id: Option<i32>, 
    }

    #[derive(Copy, Clone, Debug, EnumIter, DeriveRelation)]
    pub enum Relation {}

    impl ActiveModelBehavior for ActiveModel {}
}

pub mod stats {
    use sea_orm::entity::prelude::*;
    use serde::Serialize;

    #[derive(Clone, Debug, PartialEq, DeriveEntityModel, Serialize)]
    #[sea_orm(table_name = "drop_stats")]
    pub struct Model {
        #[sea_orm(primary_key, auto_increment = false)]
        pub zone_id: i32,
        #[sea_orm(primary_key, auto_increment = false)]
        pub item_name: String,
        pub source_mob: Option<String>,
        pub drop_count: i64,
        pub total_quantity: i64,
    }

    #[derive(Copy, Clone, Debug, EnumIter, DeriveRelation)]
    pub enum Relation {}

    impl ActiveModelBehavior for ActiveModel {}
}