package main

import (
	"os"
	"fmt"
	"time"
	"golang.org/x/crypto/bcrypt"
	"github.com/jackc/pgx"
	http "./valyala/fasthttp"
)

var bcryptWorkFactor = 12

var pool *pgx.ConnPool

func main() {
	var err error
	pool, err = pgx.NewConnPool(extractSqlConfig())

	if err != nil {
		fmt.Printf("Something went wrong while trying to open the database: %s", err)
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

func registerPost(ctx *http.RequestCtx) {
	username := ctx.FormValue("username");
	password := ctx.FormValue("password");
	var hashedPassword, _ = bcrypt.GenerateFromPassword(password, bcryptWorkFactor)

	pool.Exec("insert into users values ($1, $2)", &username, &hashedPassword)

	fmt.Fprintf(ctx, "{}")
}

func videoPost(ctx *http.RequestCtx) {
	if authenticateRequest(ctx) {
		fileHeader, error := ctx.FormFile("video")

		if error != nil {
			fmt.Printf("Something went wrong while trying to save the file: %s \n", error)
		}

		http.SaveMultipartFile(fileHeader, "C:\\test\\file.mp4")
	} else {
		ctx.Error("{}", http.StatusUnauthorized)
	}
}

func jsonPost(ctx *http.RequestCtx) {
	if authenticateRequest(ctx) {
		fileHeader, error := ctx.FormFile("jsonFile")

		if error != nil {
			fmt.Printf("Something went wrong while trying to save the file: %s \n", error)
		}

		http.SaveMultipartFile(fileHeader, "C:\\test\\test.json")
	} else {
		ctx.Error("{}", http.StatusUnauthorized)
	}
}

func authenticateRequest(ctx *http.RequestCtx) bool{
	username := ctx.FormValue("username");
	password := ctx.FormValue("password");

	var storedPassword []byte

	err := pool.QueryRow("select pass from users where username = $1", &username).Scan(&storedPassword)
	if err != nil {
		fmt.Printf("Something went wrong while querying for a password: %s \n", err)
	}

	fmt.Println(string(storedPassword[:]))
	fmt.Println(string(password[:]))
	var error = bcrypt.CompareHashAndPassword(storedPassword, password)

	if error != nil {
		fmt.Printf("Login by %s: failed. error: %s \n", username, error)
		return false
	} else {
		fmt.Printf("Login by %s: success \n", username)
		return true
	}
}

func timeToString() string {
	now := time.Now()
	return fmt.Sprintf("%02d:%02d:%02d.%03d", now.Hour(), now.Minute(), now.Second(), now.Nanosecond() / 1000 / 1000)
}
