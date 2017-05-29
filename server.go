package main

import (
	"fmt"
	http "github.com/valyala/fasthttp"
)

func main() {
	//http.ListenAndServe(":80", HTTPHandler)
	h := &http.Server{
		Handler: HTTPHandler,
		MaxRequestBodySize: 1 * 1024 * 1024 * 1024,
	}
	h.ListenAndServe(":80")
}

func HTTPHandler(ctx *http.RequestCtx) {
	ctx.SetContentType("application/json")
	fmt.Println("HttpHandler")
	fmt.Printf("%s\n", ctx.Path())

	switch string(ctx.Path()) {
		case "/":
			if ctx.IsGet() {
				ctx.WriteString("Home, GET\n")

			} else if ctx.IsPost() {
				ctx.WriteString("Home, POST\n")

			} else {
				ctx.Error("{}", http.StatusNotFound)
			}


		case "/video":
			fmt.Println("case /video")
			if ctx.IsPost() {
				fmt.Println("is post")
				file, error := ctx.FormFile("video")
				if error != nil {
					fmt.Println("ERROR ERROR")
				}
				fmt.Println("Going to save!")
				fmt.Println(file)
				http.SaveMultipartFile(file, "C:\\test\\file.mp4")

			} else {
				ctx.Error("{}", http.StatusNotFound)
			}


		case "/json":
			fmt.Println("case /json")
			if ctx.IsPost() {
				fmt.Println("is post")
				file, error := ctx.FormFile("jsonFile")
				if error != nil {
					fmt.Println("ERROR ERROR")
				}
				fmt.Println("Going to save!")
				fmt.Println(file)
				http.SaveMultipartFile(file, "C:\\test\\test.json")

			} else {
				ctx.Error("{}", http.StatusNotFound)
			}


		default:
			ctx.Error("{}", http.StatusNotFound)
	}
}
