BEGIN;

ALTER TABLE statistics_general_minutes
ADD COLUMN  count_items_user_cache integer;

ALTER TABLE statistics_general_minutes
ADD COLUMN  count_items_upload_cache integer;

ALTER TABLE statistics_general_hours
ADD COLUMN  count_items_user_cache integer;

ALTER TABLE statistics_general_hours
ADD COLUMN  count_items_upload_cache integer;

ALTER TABLE statistics_general_days
ADD COLUMN count_items_user_cache integer;

ALTER TABLE statistics_general_days
ADD COLUMN  count_items_upload_cache integer;

END;
