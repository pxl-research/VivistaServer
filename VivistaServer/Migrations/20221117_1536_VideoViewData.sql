CREATE TABLE public.video_view_data
(
    videoid uuid PRIMARY KEY REFERENCES public.videos,
    histogram bytea NOT NULL
)
TABLESPACE pg_default;

