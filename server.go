package main

import (
	"fmt"
	"time"
	"golang.org/x/crypto/bcrypt"
	http "./valyala/fasthttp"
)

var bcryptWorkFactor = 12

func main() {
	h := &http.Server{
		Handler: HTTPHandler,
		MaxRequestBodySize: 1 * 1024 * 1024 * 1024,
	}
	h.ListenAndServe(":80")
}

func HTTPHandler(ctx *http.RequestCtx) {
	fmt.Printf("%s: %s\n", timeToString(), ctx.Path())

	ctx.SetContentType("application/json")

	switch string(ctx.Path()) {
		case "/":
			if ctx.IsGet() {

			} else if ctx.IsPost() {

			} else {
				ctx.Error("{}", http.StatusNotFound)
			}

		case "/register":
			if ctx.IsPost() {
				username := ctx.FormValue("username");
				password := ctx.FormValue("password");
				var hashedPassword, _ = bcrypt.GenerateFromPassword(password, bcryptWorkFactor)
				fmt.Println(username)
				fmt.Println(hashedPassword)
				//Store record in db
				fmt.Fprintf(ctx, "{}")
			} else {
				ctx.Error("{}", http.StatusNotFound)
			}

		case "/video":
			if ctx.IsPost() {
				if authenticateRequest(ctx) {
					fileHeader, error := ctx.FormFile("video")

					if error != nil {
						fmt.Printf("Something went wrong while trying to save the file: %s", error)
					}

					http.SaveMultipartFile(fileHeader, "C:\\test\\file.mp4")
				} else {
					ctx.Error("{}", http.StatusUnauthorized)
				}
			} else {
				ctx.Error("{}", http.StatusNotFound)
			}

		case "/json":
			if ctx.IsPost() {
				if authenticateRequest(ctx) {
					fileHeader, error := ctx.FormFile("jsonFile")

					if error != nil {
						fmt.Printf("Something went wrong while trying to save the file: %s", error)
					}

					http.SaveMultipartFile(fileHeader, "C:\\test\\test.json")
				} else {
					ctx.Error("{}", http.StatusUnauthorized)
				}
			} else {
				ctx.Error("{}", http.StatusNotFound)
			}

		default:
			ctx.Error("{}", http.StatusNotFound)
	}
}

func authenticateRequest(ctx *http.RequestCtx) bool{
	username := ctx.FormValue("username");
	password := ctx.FormValue("password");
	fmt.Println(username)

	//Get record from db
	var storedPassword []byte

	var error = bcrypt.CompareHashAndPassword(storedPassword, password)
	if error != nil {
		return false
	} else {
		return true
	}
}

func timeToString() string {
	now := time.Now()
	return fmt.Sprintf("%02d:%02d:%02d.%03d", now.Hour(), now.Minute(), now.Second(), now.Nanosecond() / 1000 / 1000)
}
