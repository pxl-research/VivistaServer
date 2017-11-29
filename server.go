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
	"strconv"
	"strings"
	"time"
	"errors"
)

const bcryptWorkFactor = 12

const filePath = "C:\\test\\"

var SSL = false
var dbPool *pgx.ConnPool
var maxRequestBodySize = 1 * 1024 * 1024 * 1024
var sessionExpiry = time.Duration(1)*time.Hour + time.Duration(0)*time.Minute
var fileHandler = http.FSHandler(filePath, 0)

type video struct {
	Uuid         []byte    `json:"uuid"`
	Userid       int       `json:"userid"`
	Username     string    `json:"username"`
	Timestamp    time.Time `json:"timestamp"`
	Downloadsize int       `json:"downloadsize"`

	Title       string `json:"title"`
	Description string `json:"description"`
	Length      int    `json:"length"`
}



func main() {
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



func HTTPHandler(ctx *http.RequestCtx) {
	if SSL && !ctx.IsTLS() {
		ctx.Error("{}", http.StatusUpgradeRequired)
		return
	}
	fmt.Printf("%s: %s %s\n", timeToString(), ctx.Method(), ctx.RequestURI())

	ctx.SetContentType("application/json")
	var p = string(ctx.Path())

	//NOTE(Simon): Index
	if p == "/" {
		if ctx.IsGet() {
			indexGet(ctx)
		} else {
			ctx.Error("{}", http.StatusNotFound)
		}


	//NOTE(Simon): Register
	} else if p == "/register" {
		if ctx.IsPost() {
			registerPost(ctx)
		} else {
			ctx.Error("{}", http.StatusNotFound)
		}


	//NOTE(Simon): Login
	} else if p == "/login" {
		if ctx.IsPost() {
			loginPost(ctx)
		} else {
			ctx.Error("{}", http.StatusNotFound)
		}


	//NOTE(Simon): Video
	} else if strings.HasPrefix(p, "/video") {
		if ctx.IsGet() {
			newUrl, err := rewriteFsUrl(ctx.Path(), "main.mp4")

			if err == nil {
				request := &ctx.Request
				request.SetRequestURIBytes(newUrl)
				fileHandler(ctx)
			} else {
				ctx.Error("{}", http.StatusNotFound)
			}
		} else if ctx.IsPost() {
			videoPost(ctx)
		} else {
			ctx.Error("{}", http.StatusNotFound)
		}


	//NOTE(Simon): Meta
	} else if strings.HasPrefix(p, "/meta") {
		if ctx.IsGet() {
			newUrl, err := rewriteFsUrl(ctx.Path(), "meta.json")

			if err == nil {
				request := &ctx.Request
				request.SetRequestURIBytes(newUrl)
				fileHandler(ctx)
			} else {
				ctx.Error("{}", http.StatusNotFound)
			}
		} else {
			ctx.Error("{}", http.StatusNotFound)
		}


	//NOTE(Simon): All extras
	} else if strings.HasPrefix(p, "/extras") {
		if ctx.IsGet() {
			allExtrasGet(ctx)
		} else if ctx.IsPost() {
			allExtrasPost(ctx)
		} else {
			ctx.Error("{}", http.StatusNotFound)
		}


	//NOTE(Simon): One extra
	} else if strings.HasPrefix(p, "/extra") {
		if ctx.IsGet() {
			index := ctx.QueryArgs().Peek("index")
			newUrl, err := rewriteFsUrl(ctx.Path(), "extra" + string(index))

			if err == nil {
				request := &ctx.Request
				request.SetRequestURIBytes(newUrl)
				fileHandler(ctx)
			} else {
				ctx.Error("{}", http.StatusNotFound)
			}
		} else {
			ctx.Error("{}", http.StatusNotFound)
		}


	//NOTE(Simon): Thumbnail
	} else if strings.HasPrefix(p, "/thumbnail") {
		if ctx.IsGet() {
			newUrl, err := rewriteFsUrl(ctx.Path(), "thumb.jpg")

			if err == nil {
				request := &ctx.Request
				request.SetRequestURIBytes(newUrl)
				fileHandler(ctx)
			} else {
				ctx.Error("{}", http.StatusNotFound)
			}
		} else {
			ctx.Error("{}", http.StatusNotFound)
		}


	//NOTE(Simon): Other
	} else {
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

	rows, err := dbPool.Query(`select v.id, v.userid, u.username, v.timestamp, v.downloadsize, v.title, v.description, v.length from videos v
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

	videoBuffer.WriteByte('[')
	for rows.Next() {
		rows.Scan(&vid.Uuid, &vid.Userid, &vid.Username, &vid.Timestamp, &vid.Downloadsize, &vid.Title, &vid.Description, &vid.Length)

		result, _ := json.Marshal(vid)
		videoBuffer.Write(result)
		videoBuffer.WriteByte(',')
		numVideos++
	}
	if numVideos > 0 {
		videoBuffer.Truncate(videoBuffer.Len() - 1)
	}
	videoBuffer.WriteByte(']')

	buffer.WriteByte('{')

	buffer.Write([]byte("\"totalcount\":"))
	buffer.Write([]byte(strconv.Itoa(numVideos)))
	buffer.WriteByte(',')

	buffer.Write([]byte("\"page\":"))
	buffer.Write([]byte(strconv.Itoa((offset / count) + 1)))
	buffer.WriteByte(',')

	buffer.Write([]byte("\"count\":"))
	buffer.Write([]byte(strconv.Itoa(count)))
	buffer.WriteByte(',')

	buffer.Write([]byte("\"videos\":"))
	buffer.Write(videoBuffer.Bytes())

	buffer.WriteByte('}')

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
	var videoid = ctx.QueryArgs().Peek("videoid")
	var vid = new(video)

	err := dbPool.QueryRow(`select v.id, v.userid, u.username, v.timestamp, v.downloadsize from videos v
						inner join users u on v.userid = u.userid
						where v.id = $1`, &videoid).Scan(&vid.Uuid, &vid.Userid, &vid.Username, &vid.Timestamp, &vid.Downloadsize)
	if (err != nil) {
		logError("SQL error: ", err)
	}

	var videoPath = fmt.Sprintf("%s%s\\main.mp4", filePath, vid.Uuid)
	logDebug(string(videoPath))

	exists, err := pathExists(videoPath)
	if (err != nil) {
		logError("can't open file: ", err)
	}

	if (exists) {
		ctx.SendFileBytes([]byte(videoPath))
	}
}

func videoPost(ctx *http.RequestCtx) {
	if authenticateToken(ctx) {
		jsonHeader, err := ctx.FormFile("meta")
		videoHeader, err := ctx.FormFile("video")
		thumbHeader, err := ctx.FormFile("thumb")
		uuid := ctx.FormValue("uuid")

		if err != nil {
			logError("Something went wrong while trying to save the file", err)
			ctx.Error("{}", http.StatusInternalServerError)
			return
		}

		path := fmt.Sprintf("%s%s\\", filePath, uuid)
		metaFilename := fmt.Sprintf("%s\\meta.json", path)
		videoFilename := fmt.Sprintf("%s\\main.mp4", path)
		thumbFilename := fmt.Sprintf("%s\\thumb.jpg", path)

		os.MkdirAll(path, os.ModeDir)

		err = http.SaveMultipartFile(jsonHeader, metaFilename)
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

		meta, _ := readStringFromFile(metaFilename, 0)

		var startIndex int
		var inc int

		_, inc = extractJsonValue(meta[startIndex:]) //uuid
		startIndex += inc
		title, inc := extractJsonValue(meta[startIndex:])
		startIndex += inc
		description, inc := extractJsonValue(meta[startIndex:])
		startIndex += inc
		_, inc = extractJsonValue(meta[startIndex:]) //perspective
		startIndex += inc
		length, inc := extractJsonValue(meta[startIndex:])
		startIndex += inc
		floatLength, _ := strconv.ParseFloat(length, 32)
		intLength := int(floatLength)

		userid := getUserIdFromToken(string(ctx.FormValue("token")))

		if videoExists(uuid) && userOwnsVideo(uuid, userid) {
			timestamp := time.Now()
			//TODO(Simon): Update to match query below.
			_, err = dbPool.Exec("update videos set timestamp = $1 where id = $2", &timestamp, &uuid)
		} else {
			_, err = dbPool.Exec("insert into videos (id, userid, downloadsize, title, description, length)" +
									"values ($1, $2, $3, $4, $5, $6)",
									&uuid, &userid, 0, &title, &description, &intLength)
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

func allExtrasGet(ctx *http.RequestCtx) {
	var videoid = []byte(ctx.QueryArgs().Peek("videoid"))
	rows, err := dbPool.Query(`select index from extra_files
							where video_id = $1`, &videoid)
	if err != nil {
		logError("Something went wrong while loading the index", err)
		ctx.Error("{}", http.StatusInternalServerError)
		return
	}

	var buffer bytes.Buffer
	buffer.WriteByte('[')

	var count int
	for rows.Next() {
		var index = 0
		rows.Scan(&index)

		buffer.Write([]byte(strconv.Itoa(index)))
		buffer.WriteByte(',')

		count++
	}

	if count > 0 {
		buffer.Truncate(buffer.Len() - 1)
	}

	buffer.WriteByte(']')

	ctx.SetBody(buffer.Bytes())
}

func allExtrasPost(ctx *http.RequestCtx) {
	if authenticateToken(ctx) {
		var ids []int
		var uuid = ctx.FormValue("uuid")
		var indices = string(ctx.FormValue("indices"))
		var splitIndices = strings.Split(indices, ",")

		userid := getUserIdFromToken(string(ctx.FormValue("token")))

		if videoExists(uuid) && userOwnsVideo(uuid, userid) {
			for i := 0; i < len(splitIndices); i++ {
				var parsed, _ = strconv.ParseInt(splitIndices[i], 0, 32)
				ids = append(ids, int(parsed))
			}

			path := fmt.Sprintf("%s%s\\", filePath, uuid)

			var buffer = bytes.NewBufferString("insert into extra_files (video_id, index) values ")

			//NOTE(Simon): Fill query buffer
			for i := 0; i < len(ids); i++ {
				filename := fmt.Sprintf("extra%d", ids[i])
				header, _ := ctx.FormFile(filename)
			 	extraPath := fmt.Sprintf("%s\\%s", path, filename)
		 	fmt.Printf("header: %v \n", filename)
		 	fmt.Printf("header: %v \n", header)
		 	fmt.Printf("header: %v \n", extraPath)

			 	//TODO(Simon): Unsafe, no escaping in this query!
			 	buffer.WriteString("('")
			 	buffer.Write([]byte(uuid))
			 	buffer.WriteString("',")
			 	buffer.WriteString(strconv.Itoa(ids[i]))
		 		buffer.WriteString("),")

				err := http.SaveMultipartFile(header, extraPath)
				if err != nil {
					ctx.Error("{}", http.StatusInternalServerError)
					return;
				}
			}

			buffer.Truncate(buffer.Len() - 1);
			logDebug(fmt.Sprintf("delete from extra_files where video_id = '%v'", string(uuid)))
			dbPool.Exec(fmt.Sprintf("delete from extra_files where video_id = '%v'", string(uuid)))
			_, err := dbPool.Exec(buffer.String())

			if err != nil {
				logError("Error while inserting extra_files rows", err)
			}

			ctx.WriteString("{}")
		} else {
			ctx.Error("{}", http.StatusUnauthorized)
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



func rewriteFsUrl(url []byte, filename string) ([]byte, error) {
	parts := bytes.Split(url, []byte("/"))

	if (len(parts) != 3) {
		return url, errors.New("bad url")
	}

	newUrl := []byte("/")
	newUrl = append(newUrl, parts[2]...)
	newUrl = append(newUrl, []byte("/")...)
	newUrl = append(newUrl, []byte(filename)...)

	return newUrl, nil
}

func timeToString() string {
	now := time.Now()
	return fmt.Sprintf("%02d:%02d:%02d.%03d", now.Hour(), now.Minute(), now.Second(), now.Nanosecond()/1000/1000)
}

func logError(message string, err error) {
	fmt.Printf(timeToString() + " Error: " + message + ": %s \n", err)
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

func extractJsonValue(json string) (string, int) {
	startIndex := strings.Index(json, ":")
	endIndex := strings.Index(json, "\n")

	return json[startIndex+1 : endIndex-1], endIndex + 1
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

// exists returns whether the given file or directory exists or not
func pathExists(path string) (bool, error) {
    _, err := os.Stat(path)
    if err == nil { return true, nil }
    if os.IsNotExist(err) { return false, nil }
    return true, err
}
