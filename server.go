package main

import (
	"os"
	"fmt"
	"time"
	"bytes"
	"strings"
	"runtime"
	"io/ioutil"
	"encoding/json"
	"github.com/jackc/pgx"
	http "./valyala/fasthttp"
	"golang.org/x/crypto/bcrypt"
	"crypto/rand"
	"encoding/base64"
)

const bcryptWorkFactor = 12

var SSL = false
var dbPool *pgx.ConnPool
var maxRequestBodySize = 1 * 1024 * 1024 * 1024
var sessionExpiry = time.Duration(1) * time.Hour + time.Duration(0) * time.Minute

type video struct {
	Uuid []byte
	Userid int
	Timestamp time.Time
	Downloadsize int
}

func main() {
	runtime.GOMAXPROCS(runtime.NumCPU() - 1)
	var err error;
	dbPool, err = pgx.NewConnPool(extractSqlConfig())

	if err != nil {
		logError("Something went wrong while trying to open the database", err)
		os.Exit(1)
	}

	fmt.Println("Connected to db")

	h := &http.Server{
		Handler: HTTPHandler,
		MaxRequestBodySize: maxRequestBodySize,
	}

	fmt.Println("Starting server")
	if SSL {
		err = h.ListenAndServeTLS(":443", "C:\\Users\\Simon\\Documents\\Git\\360Server\\valyala\\fasthttp\\ssl-cert-snakeoil.pem", "C:\\Users\\Simon\\Documents\\Git\\360Server\\valyala\\fasthttp\\ssl-cert-snakeoil.key")
	} else {
		err = h.ListenAndServe(":80")
	}
	if err != nil {
		logError("failed to start server", err)
	}
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
	if SSL && !ctx.IsTLS() {
		ctx.Error("{}", http.StatusUpgradeRequired)
		return
	}

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

		case "/login":
			if ctx.IsPost() {
				loginPost(ctx)
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

	rows, err := dbPool.Query("select id, userid, timestamp, downloadsize from videos limit $1 offset $2", count, offset)
	if err != nil {
		logError("Something went wrong while loading the index", err)
		ctx.Error("{}", http.StatusInternalServerError)
		return
	}

	defer rows.Close()

	var vid video;
	var buffer bytes.Buffer

	buffer.Write([]byte("["))
	for rows.Next() {
		rows.Scan(&vid.Uuid, &vid.Userid, &vid.Timestamp, &vid.Downloadsize)

		result, _ := json.Marshal(vid)
		buffer.Write(result)
		buffer.Write([]byte(","))
	}
	buffer.Truncate(buffer.Len() - 1)
	buffer.Write([]byte("]"))

	ctx.SetBody(buffer.Bytes())
}

func registerPost(ctx *http.RequestCtx) {
	username := strings.ToLower(string(ctx.FormValue("username")))

	if len(strings.TrimSpace(username)) > 0 && !userExists(username) {
		password := ctx.FormValue("password")
		var hashedPassword, _ = bcrypt.GenerateFromPassword(password, bcryptWorkFactor)

		_, err := dbPool.Exec("insert into users (username, pass) values ($1, $2)", &username, &hashedPassword)

		if err != nil {
			ctx.Error("{}", http.StatusInternalServerError)
			return
		}
	} else {
		ctx.Error("{}", http.StatusConflict)
		return
	}

	token := newToken(32)
	expiry := time.Now().Add(sessionExpiry)
	userid := getUserId(username)

	dbPool.Exec("insert into sessions (token, expiry, userid) values ($1, $2)", token, expiry, userid)

	fmt.Fprintf(ctx, token)
}

func loginPost(ctx *http.RequestCtx) {
	username := strings.ToLower(string(ctx.FormValue("username")))
	success, err := authenticatePassword(ctx)

	if err != nil {
		ctx.Error("{}", http.StatusInternalServerError)
	} else if success {
		token := newToken(32)
		expiry := time.Now().Add(sessionExpiry)
		userid := getUserId(username)

		dbPool.Exec("insert into sessions (token, expiry, userid) values ($1, $2, $3)", token, expiry, userid)

		fmt.Fprintf(ctx, token)
	} else {
		ctx.Error("{}", http.StatusUnauthorized)
	}
}

func videoPost(ctx *http.RequestCtx) {
	if authenticateToken(ctx) {
		fileHeader, err := ctx.FormFile("video")
		uuid := ctx.FormValue("uuid")

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
			timestamp := time.Now()
			_, err = dbPool.Exec("update videos set timestamp = $1 where id = $2", &timestamp, &uuid)
		} else{
			userid := getUserId(string(ctx.FormValue("username")))
			_, err = dbPool.Exec("insert into videos (id, userid) values ($1, $2)", &uuid, &userid)
		}

	} else {
		ctx.Error("{}", http.StatusUnauthorized)
	}
}

func jsonPost(ctx *http.RequestCtx) {
	if authenticateToken(ctx) {
		fileHeader, err := ctx.FormFile("jsonFile")
		uuid := ctx.FormValue("uuid")

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
			timestamp := time.Now()
			_, err = dbPool.Exec("update videos set timestamp = $1 where id = $2", &timestamp, &uuid)
		} else{
			userid := getUserId(string(ctx.FormValue("username")))
			_, err = dbPool.Exec("insert into videos (id, userid) values ($1, $2)", &uuid, &userid)
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

func authenticatePassword(ctx *http.RequestCtx) (bool, error) {
	username := strings.ToLower(string(ctx.FormValue("username")))
	password := ctx.FormValue("password")

	var storedPassword []byte

	err := dbPool.QueryRow("select pass from users where username = $1", &username).Scan(&storedPassword)

	//Note(Simon): If the error is no rows, we know the user does not exist. But to prevent timing based attacks we're going to run it through bcrypt anyway.
	//Note(cont.): All other errors should be real errors and warrant a 500 response.
	if err != nil && err != pgx.ErrNoRows {
		return false, err
	}

	var error = bcrypt.CompareHashAndPassword(storedPassword, password)

	if error != nil {
		return false, nil
	} else {
		return true, nil
	}
}

func authenticateToken(ctx *http.RequestCtx) bool {
	token := string(ctx.FormValue("token"))
	validUntil := time.Time{}

	err := dbPool.QueryRow("select expiry from sessions where token = $1", &token).Scan(&validUntil)

	if err != nil {
		return false
	} else if validUntil.Before(time.Now()) {
		//TODO(Simon): Remove from db
		dbPool.Exec("delete from sessions where token = $1", &token)

		return false
	} else {
		newExpiry := time.Now().Add(sessionExpiry)
		dbPool.Exec("update sessions set expiry = $1 where token = $2", &newExpiry, &token)

		return true
	}

	return true
}

func getUserId(username string) int {
	id := 0
	err := dbPool.QueryRow("select userid from users where username = $1", &username).Scan(&id)
	if err != nil {
		logError("Something went wrong while querying for a password", err)
		return id
	}

	return id
}

func userExists(username string) bool {
	count := 0
	err := dbPool.QueryRow("select count(*) from users where username=$1", username).Scan(&count)
	if err != nil {
		logError("Couldn't verify whether key exists", err)
		return false
	}
	if count <= 0 {
		return false
	} else {
		return true
	}
}

func videoExists(uuid []byte) bool {
	count := 0
	err := dbPool.QueryRow("select count(*) from videos where id=$1", uuid).Scan(&count)
	if err != nil {
		logError("Couldn't verify whether key exists", err)
		return false
	}
	if count <= 0 {
		return false
	} else {
		return true
	}
}

func newToken(length int) string {
    randomBytes := make([]byte, 32)
    _, err := rand.Read(randomBytes)

    if err != nil {
    	//Note(Simon): Errors here are bad enough that a panic is warranted.
        panic(err)
    }

    return base64.StdEncoding.EncodeToString(randomBytes)[:length]
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
