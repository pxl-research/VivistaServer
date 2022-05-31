CREATE TABLE IF NOT EXISTS public.playlists
(
    id uuid NOT NULL,
    name text NOT NULL,
    userid integer NOT NULL,
    privacy integer NOT NULL DEFAULT 0,
    count integer NOT NULL DEFAULT 0,
    CONSTRAINT playlists_pkey PRIMARY KEY (id),
    CONSTRAINT userid FOREIGN KEY (userid)
        REFERENCES public.users (userid) MATCH SIMPLE
        ON UPDATE CASCADE
        ON DELETE CASCADE
        NOT VALID
)


CREATE TABLE public.playlist_videos
(
    playlistid uuid NOT NULL,
    videoid uuid NOT NULL,
    index integer NOT NULL,
    CONSTRAINT playlist_videos_pkey PRIMARY KEY (playlistid, videoid),
    CONSTRAINT playlistid FOREIGN KEY (playlistid)
        REFERENCES public.playlists (id) MATCH SIMPLE
        ON UPDATE CASCADE
        ON DELETE CASCADE
        NOT VALID,
    CONSTRAINT videoid FOREIGN KEY (videoid)
        REFERENCES public.videos (id) MATCH SIMPLE
        ON UPDATE CASCADE 
        ON DELETE CASCADE
        NOT VALID
)

