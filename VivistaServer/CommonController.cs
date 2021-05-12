using System;
using System.Collections.Generic;
using System.IO;
using System.Security.Cryptography;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Http;

namespace VivistaServer
{
	public class Pagination
	{
		public int page;
		public int pageCount;
		public int totalCount;
		public int countPerPage;
		public int count;
		public int offset;
		public List<int> pageNumbers = new List<int>();

		public Pagination(int totalCount, int countPerPage, int offset)
		{
			this.totalCount = totalCount;
			this.countPerPage = countPerPage;
			this.offset = offset;

			Update();
		}

		public void Update()
		{
			page = offset / countPerPage + 1;
			pageCount = (int)MathF.Ceiling(totalCount / (float)countPerPage);
			count = page < pageCount ? countPerPage : totalCount - (pageCount - 1) * countPerPage;

			pageNumbers.Add(1);

			if (pageCount > 1)
			{
				int start = Math.Clamp(page - 2, 2, Math.Max(pageCount - 1, 2));
				int end = Math.Clamp(page + 3, 2, Math.Max(pageCount, 2));
				for (int i = start; i < end; i++)
				{
					pageNumbers.Add(i);
				}

				pageNumbers.Add(pageCount);
			}
		}
	}

	public class CommonController
	{
		private const int kb = 1024;
		private const int mb = 1024 * kb;
		private const int gb = 1024 * mb;

		private const int fileBufferSize = 16 * kb;

		public static string baseURL;
		public static string wwwroot;

		public static async Task WriteFile(HttpContext context, string filename, string contentType, string responseFileName)
		{
			//TODO(Simon): Pooling of buffers?
			byte[] buffer = new byte[fileBufferSize];

			try
			{
				context.Response.ContentType = contentType;
				context.Response.Headers["Content-Disposition"] = "attachment; filename=" + responseFileName;
				context.Response.ContentLength = new FileInfo(filename).Length;

				using (var stream = File.OpenRead(filename))
				{
					int read;
					while ((read = await stream.ReadAsync(buffer, 0, buffer.Length)) > 0)
					{
						await context.Response.Body.WriteAsync(buffer.AsMemory(0, read));
					}
				}
			}
			catch (FileNotFoundException)
			{
				await Write404(context);
			}
			catch (Exception e)
			{
				await WriteError(context, "Something went wrong while reading this file", StatusCodes.Status500InternalServerError, e);
			}
		}

		//NOTE(Simon): Exception is only output in DEBUG
		public static async Task WriteError(HttpContext context, string error, int errorCode, Exception e = null)
		{
			context.Response.StatusCode = errorCode;
			await context.Response.WriteAsync($"{{\"error\": \"{error}\"}}");
#if DEBUG
			if (e != null)
			{
				await context.Response.WriteAsync(Environment.NewLine);
				await context.Response.WriteAsync(e.ToString());
				Console.WriteLine(e.StackTrace);
			}
#endif
		}

		public static async Task Write404(HttpContext context, string message = "File Not Found")
		{
			await WriteError(context, message, StatusCodes.Status404NotFound);
		}

		[Route("", "404")]
		private static async Task Error404(HttpContext context)
		{
			await Write404(context);
		}

		public static void SetJSONContentType(HttpContext context)
		{
			context.Response.ContentType = "application/json";
		}

		public static void SetHTMLContentType(HttpContext context)
		{
			context.Response.ContentType = "text/html";
		}
	}
}