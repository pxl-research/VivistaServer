CREATE TABLE public.questions 
(
	id uuid PRIMARY KEY,
	videoid uuid NOT NULL,
	question text NOT NULL,
	questiontype integer NOT NULL,
	potential_answers text,
	correct_answer smallint,
	CONSTRAINT questions_videos_videoid FOREIGN KEY (videoid)
		REFERENCES public.videos (id) MATCH SIMPLE
		ON UPDATE CASCADE
		ON DELETE CASCADE
)
TABLESPACE pg_default;


CREATE TABLE public.user_answers 
(
	id SERIAL PRIMARY KEY,
	timestamp timestamp,
	question_id uuid,
	userid integer,
	total_score smallint,
	CONSTRAINT user_answers_questions_questionid FOREIGN KEY (question_id)
		REFERENCES public.questions (id) MATCH SIMPLE
		ON UPDATE CASCADE
		ON DELETE CASCADE,
	CONSTRAINT user_answers_users_userid FOREIGN KEY (userid)
		REFERENCES public.users (userid) MATCH SIMPLE
		ON UPDATE CASCADE
		ON DELETE CASCADE
)
TABLESPACE pg_default;

CREATE TABLE public.answers
(
	id SERIAL PRIMARY KEY,
	question_id uuid,
	user_answers_id integer,
	score smallint,
	answer_index smallint,
	CONSTRAINT answers_questions_questionid FOREIGN KEY (question_id)
		REFERENCES public.questions (id) MATCH SIMPLE
		ON UPDATE CASCADE
		ON DELETE CASCADE,
	CONSTRAINT answers_user_answers_useranswerid FOREIGN KEY (user_answers_id)
		REFERENCES public.user_answers (id) MATCH SIMPLE
		ON UPDATE CASCADE
		ON DELETE CASCADE
)
TABLESPACE pg_default;