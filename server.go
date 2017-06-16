package main

import (
	http "./valyala/fasthttp"
	"bytes"
	"crypto/rand"
	"encoding/base64"
	"encoding/json"
	"fmt"
	"github.com/jackc/pgx"
	"golang.org/x/crypto/bcrypt"
	"io/ioutil"
	"os"
	"runtime"
	"strconv"
	"strings"
	"time"
)

const bcryptWorkFactor = 12

var SSL = false
var dbPool *pgx.ConnPool
var maxRequestBodySize = 1 * 1024 * 1024 * 1024
var sessionExpiry = time.Duration(1)*time.Hour + time.Duration(0)*time.Minute

type video struct {
	Uuid         []byte    `json:"uuid"`
	Userid       int       `json:"userid"`
	Username     string    `json:"username"`
	Timestamp    time.Time `json:"timestamp"`
	Downloadsize int       `json:"downloadsize"`

	Title     string `json:"title"`
	Thumbnail string `json:"thumbnail"`
	Length    int    `json:"length"`
}

func main() {
	runtime.GOMAXPROCS(runtime.NumCPU() - 1)
	var err error
	dbPool, err = pgx.NewConnPool(extractSqlConfig())

	if err != nil {
		logError("Something went wrong while trying to open the database", err)
		os.Exit(1)
	}

	fmt.Println("Connected to db")

	h := &http.Server{
		Handler:            HTTPHandler,
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
		if ctx.IsGet() {
			videoGet(ctx)
		}
		if ctx.IsPost() {
			videoPost(ctx)
		} else {
			ctx.Error("{}", http.StatusNotFound)
		}

	default:
		ctx.Error("{}", http.StatusNotFound)
	}
}

func indexGet(ctx *http.RequestCtx) {
	args := ctx.QueryArgs()
	offset, err := args.GetUint("offset")
	if err != nil || offset < 0 {
		offset = 0
	}

	count, err := args.GetUint("count")
	if err != nil || count <= 0 || count > 100 {
		count = 10
	}

	var author = string(args.Peek("author"))
	var userid pgx.NullInt32
	if author != "" {
		userid.Int32 = int32(getUserId(author))
		userid.Valid = true
	} else {
		userid.Valid = false
	}

	var uploadDate pgx.NullTime
	temp, err := args.GetUint("agedays")
	if err != nil {
		uploadDate.Valid = false
	} else {
		uploadDate.Valid = true
		uploadDate.Time = time.Now().AddDate(0, 0, -temp)
	}

	rows, err := dbPool.Query(`select v.id, v.userid, u.username, v.timestamp, v.downloadsize from videos v
								inner join users u on v.userid = u.userid
								where ($1::int is NULL or v.userid=$1)
								and ($2::timestamp is NULL or v.timestamp>=$2)
								order by v.timestamp desc
								limit $3
								offset $4`, &userid, &uploadDate, &count, &offset)
	if err != nil {
		logError("Something went wrong while loading the index", err)
		ctx.Error("{}", http.StatusInternalServerError)
		return
	}

	var vid video
	var videoBuffer bytes.Buffer
	var buffer bytes.Buffer
	var numVideos int

	videoBuffer.Write([]byte("["))
	for rows.Next() {
		//TODO(Simon): Get video title, thumbail, length
		rows.Scan(&vid.Uuid, &vid.Userid, &vid.Username, &vid.Timestamp, &vid.Downloadsize)

		result, _ := json.Marshal(vid)
		videoBuffer.Write(result)
		videoBuffer.Write([]byte(","))
		numVideos++
	}
	if numVideos > 0 {
		videoBuffer.Truncate(videoBuffer.Len() - 1)
	}
	videoBuffer.Write([]byte("]"))

	buffer.Write([]byte("{"))

	buffer.Write([]byte("\"totalcount\":"))
	buffer.Write([]byte(strconv.Itoa(numVideos)))
	buffer.Write([]byte(","))

	buffer.Write([]byte("\"page\":"))
	buffer.Write([]byte(strconv.Itoa((offset / count) + 1)))
	buffer.Write([]byte(","))

	buffer.Write([]byte("\"count\":"))
	buffer.Write([]byte(strconv.Itoa(count)))
	buffer.Write([]byte(","))

	buffer.Write([]byte("\"videos\":"))
	buffer.Write(videoBuffer.Bytes())

	buffer.Write([]byte("}"))

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

func videoGet(ctx *http.RequestCtx) {
	/*
		var videoid = ctx.QueryArgs().Peek("videoid")
		var vid = new(video)

		dbPool.QueryRow(`select v.id, v.userid, u.username, v.timestamp, v.downloadsize, from videos v
							inner join users u on v.userid = u.userid`, videoid).Scan(&vid.Uuid, &vid.Userid, &vid.Username, &vid.Timestamp, &vid.Downloadsize)
		var jsonFilename = fmt.Sprintf("C:\\test\\%s.json", vid.Uuid);
		var videoFilename = fmt.Sprintf("C:\\test\\%s.mp4", vid.Uuid);

		var json, err = ioutil.ReadFile(jsonFilename);
	*/
}

func videoPost(ctx *http.RequestCtx) {
	if authenticateToken(ctx) {
		jsonHeader, err := ctx.FormFile("jsonFile")
		videoHeader, err := ctx.FormFile("video")
		thumbHeader, err := ctx.FormFile("thumb")
		uuid := ctx.FormValue("uuid")

		if err != nil {
			logError("Something went wrong while trying to save the file", err)
			ctx.Error("{}", http.StatusInternalServerError)
			return
		}

		jsonFilename := fmt.Sprintf("C:\\test\\%s.json", uuid)
		videoFilename := fmt.Sprintf("C:\\test\\%s.mp4", uuid)
		thumbFilename := fmt.Sprintf("C:\\test\\%s.jpg", uuid)

		err = http.SaveMultipartFile(jsonHeader, jsonFilename)
		if err == nil {
			err = http.SaveMultipartFile(videoHeader, videoFilename)
			if err == nil {
				err = http.SaveMultipartFile(thumbHeader, thumbFilename)
			}
		}

		if err != nil {
			logError("Something went wrong while trying to save the file", err)
			ctx.Error("{}", http.StatusInternalServerError)
			return
		}

		json, _ := readStringFromFile(jsonFilename, 0)

		fmt.Println(json)

		userid := getUserIdFromToken(string(ctx.FormValue("token")))

		if videoExists(uuid) && userOwnsVideo(uuid, userid) {
			timestamp := time.Now()
			_, err = dbPool.Exec("update videos set timestamp = $1 where id = $2", &timestamp, &uuid)
		} else {
			_, err = dbPool.Exec("insert into videos (id, userid) values ($1, $2)", &uuid, &userid)
		}

		if err != nil {
			logError("Something went wrong while inserting video data in database", err)
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
		return -1
	}

	return id
}

func getUserIdFromToken(token string) int {
	id := 0
	err := dbPool.QueryRow("select userid from sessions where token = $1", &token).Scan(&id)
	if err != nil {
		return -1
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
	err := dbPool.QueryRow("select count(*) from videos where id=$1", &uuid).Scan(&count)
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

func userOwnsVideo(uuid []byte, userid int) bool {
	count := 0
	err := dbPool.QueryRow("select count(*) from videos where id=$1 and userid=$2", &uuid, &userid).Scan(&count)
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
	return fmt.Sprintf("%02d:%02d:%02d.%03d", now.Hour(), now.Minute(), now.Second(), now.Nanosecond()/1000/1000)
}

func logError(message string, err error) {
	fmt.Printf(timeToString()+" Error: "+message+": %s \n", err)
}

func logDebug(message string) {
	fmt.Printf(timeToString() + " Debug: " + message + "\n")
}

//NOTE(Simon): if length <= 0, read all
func readStringFromFile(filename string, length int) (string, error) {
	if length <= 0 {
		data, err := ioutil.ReadFile(filename)
		if err != nil {
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
