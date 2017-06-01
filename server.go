package main

import (
	"os"
	"fmt"
	"time"
	"io/ioutil"
	"golang.org/x/crypto/bcrypt"
	"github.com/jackc/pgx"
	http "./valyala/fasthttp"
)

const bcryptWorkFactor = 12

var pool *pgx.ConnPool

type video struct {
	uuid []byte
	userid int
	timestamp time.Time
	downloadsize int
}

func main() {
	var err error
	pool, err = pgx.NewConnPool(extractSqlConfig())

	if err != nil {
		logError("Something went wrong while trying to open the database", err)
		os.Exit(1)
	}

	fmt.Println("Connected to db")

	h := &http.Server{
		Handler: HTTPHandler,
		MaxRequestBodySize: 1 * 1024 * 1024 * 1024,
	}

	fmt.Println("Starting server")
	h.ListenAndServe(":80")
}

func extractSqlConfig() pgx.ConnPoolConfig {
	var config pgx.ConnPoolConfig

	config.Host = os.Getenv("360VIDEO_DB_HOST")
	if config.Host == "" {
		config.Host = "localhost"
	}

	config.User = os.Getenv("360VIDEO_DB_USER")
	if config.User == "" {
		config.User = "postgres"
	}

	config.Password = os.Getenv("360VIDEO_DB_PASSWORD")
	if config.Password == "" {
		config.Password = os.Getenv("USER")
	}

	config.Database = "360video"
	config.MaxConnections = 5

	return config
}

func HTTPHandler(ctx *http.RequestCtx) {
	fmt.Printf("%s: %s\n", timeToString(), ctx.Path())

	ctx.SetContentType("application/json")

	switch string(ctx.Path()) {
		case "/":
			if ctx.IsGet() {
				indexGet(ctx)
			} else {
				ctx.Error("{}", http.StatusNotFound)
			}

		case "/register":
			if ctx.IsPost() {
				registerPost(ctx)
			} else {
				ctx.Error("{}", http.StatusNotFound)
			}

		case "/video":
			if ctx.IsPost() {
				videoPost(ctx)
			} else {
				ctx.Error("{}", http.StatusNotFound)
			}

		case "/json":
			if ctx.IsPost() {
				jsonPost(ctx)
			} else {
				ctx.Error("{}", http.StatusNotFound)
			}

		default:
			ctx.Error("{}", http.StatusNotFound)
	}
}

func indexGet(ctx *http.RequestCtx) {
	offset, err := ctx.QueryArgs().GetUint("offset")
	if (err != nil || offset < 0) {
		offset = 0
	}
	count, err := ctx.QueryArgs().GetUint("count")
	if (err != nil || count <= 0 || count > 100) {
		count = 10
	}

	rows, err := pool.Query("select * from videos limit $1 offset $2", count, offset);
	if err != nil {
		logError("Something went wrong while loading the index", err)
		ctx.Error("{}", http.StatusInternalServerError)
		return
	}
	var vid video

	for rows.Next() {
		rows.Scan(&vid.uuid, &vid.userid, &vid.timestamp, &vid.downloadsize)
		fmt.Println(vid);
	}
}

func registerPost(ctx *http.RequestCtx) {
	username := ctx.FormValue("username");
	password := ctx.FormValue("password");
	var hashedPassword, _ = bcrypt.GenerateFromPassword(password, bcryptWorkFactor)

	_, err := pool.Exec("insert into users values ($1, $2)", &username, &hashedPassword)

	if err != nil {
		//TODO(Simon): Error handlng when user already exists.
	}

	fmt.Fprintf(ctx, "{}")
}

func videoPost(ctx *http.RequestCtx) {
	if authenticateRequest(ctx) {
		fileHeader, err := ctx.FormFile("video")
		uuid := ctx.FormValue("uuid");

		if err != nil {
			logError("Something went wrong while trying to save the file", err)
			ctx.Error("{}", http.StatusInternalServerError)
			return
		}

		http.SaveMultipartFile(fileHeader, fmt.Sprintf("C:\\test\\%s.mp4", uuid))

		if err != nil {
			logError("Something went wrong while trying to save the file", err)
			ctx.Error("{}", http.StatusInternalServerError)
			return
		}

		if videoExists(uuid) {
			timestamp := time.Now();
			_, err = pool.Exec("update videos set timestamp = $1", &timestamp)
		} else{
			userid := getUserId(string(ctx.FormValue("username")))
			_, err = pool.Exec("insert into videos (id, userid) values ($1, $2)", &uuid, &userid)
		}

	} else {
		ctx.Error("{}", http.StatusUnauthorized)
	}
}

func jsonPost(ctx *http.RequestCtx) {
	if authenticateRequest(ctx) {
		fileHeader, err := ctx.FormFile("jsonFile")
		uuid := ctx.FormValue("uuid");

		if err != nil {
			logError("Something went wrong while trying to save the file", err)
			ctx.Error("{}", http.StatusInternalServerError)
			return
		}

		err = http.SaveMultipartFile(fileHeader, fmt.Sprintf("C:\\test\\%s.json", uuid))
		if err != nil {
			logError("Something went wrong while trying to save the file", err)
			ctx.Error("{}", http.StatusInternalServerError)
			return
		}

		if videoExists(uuid) {
			timestamp := time.Now();
			_, err = pool.Exec("update videos set timestamp = $1", &timestamp)
		} else{
			userid := getUserId(string(ctx.FormValue("username")))
			_, err = pool.Exec("insert into videos (id, userid) values ($1, $2)", &uuid, &userid)
		}

		if err != nil {
			logError("Something went wrong while trying to save the file", err)
			ctx.Error("{}", http.StatusInternalServerError)
			return
		}

	} else {
		ctx.Error("{}", http.StatusUnauthorized)
	}
}

func authenticateRequest(ctx *http.RequestCtx) bool {
	username := ctx.FormValue("username");
	password := ctx.FormValue("password");

	var storedPassword []byte

	err := pool.QueryRow("select pass from users where username = $1", &username).Scan(&storedPassword)
	if err != nil {
		logError("Something went wrong while querying for a password", err)
		return false
	}

	var error = bcrypt.CompareHashAndPassword(storedPassword, password)

	if error != nil {
		return false
	} else {
		return true
	}
}

func getUserId(username string) int {
	id := 0
	err := pool.QueryRow("select userid from users where username = $1", &username).Scan(&id)
	if err != nil {
		logError("Something went wrong while querying for a password", err)
		return id
	}

	return id;
}

func videoExists(uuid []byte) bool {
	count := 0;
	err := pool.QueryRow("select count(*) from videos where id=$1", uuid).Scan(&count);
	if err != nil {
		logError("Couldn't verify whether key exists", err)
		return false
	}
	if count <= 0 {
		return false
	} else {
		return true;
	}
}

func timeToString() string {
	now := time.Now()
	return fmt.Sprintf("%02d:%02d:%02d.%03d", now.Hour(), now.Minute(), now.Second(), now.Nanosecond() / 1000 / 1000)
}

func logError(message string, err error) {
	fmt.Printf(timeToString() + " Error: " + message + ": %s \n", err)
}

func readStringFromFile(filename string, length int) (string, error) {
	if (length <= 0) {
		data, err := ioutil.ReadFile(filename)
		if (err != nil) {
			return "", err
		}
		return string(data), nil
	}

	file, err := os.Open(filename)
	defer file.Close()
	if err != nil {
		return "", err
	}

	data := make([]byte, length)
	_, err = file.Read(data)
	if err != nil {
		return "", err
	}
	return string(data), nil
}
