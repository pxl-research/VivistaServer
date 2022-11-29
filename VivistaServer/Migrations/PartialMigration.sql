'--questions
	id					guid PK
	videoid				guid FK index
	question 			text
	questiontype		int? enum?
	potentialanswers	jsonb? string?
	correctanswer		int

useranswers
	id 					int
	timestamp			timestamp
	questionid			guid FK index
	totalscore			int

answers
	id 					int
	questionid			guid FK index
	userid				guid FK index
	score				int
	answerindex			byte
--'

CREATE TABLE public.questions 
{
	id uuid PRIMARY KEY,
	videoid uuid NOT NULL,
	question text NOT NULL,
	questiontype ??? NOT NULL,
	potentialanswers ???,
	correctanswer smallint,
	CONSTRAINT questions_videos_videoid FOREIGN KEY (videoid)
		REFERENCES public.videos (id) MATCH SIMPLE
		ON UPDATE CASCADE
		ON DELETE CASCADE
}
TABLESPACE pg_default;


CREATE TABLE public.user_answers 
{
	id SERIAL PRIMARY KEY,
	timestamp timestamp,
	questionid uuid,
	totalscore smallint
}
TABLESPACE pg_default;

CREATE TABLE public.answers
{
	id SERIAL PRIMARY KEY,
	questionid guid,
	userid guid,
	score integer,
	answerindex small
}
