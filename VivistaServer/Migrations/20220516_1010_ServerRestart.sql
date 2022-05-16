CREATE TABLE server_restart (
	time timestamptz PRIMARY KEY
);

ALTER TABLE public.server_restart 
OWNER to postgres;