using System;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Http;
using Npgsql;
using Dapper;

namespace VivistaServer
{
	public class BugReportController
	{
		[Route("POST", "/api/report_bug")]
		[Route("POST", "/api/v1/report_bug")]
		private static async Task ReportBugPostApi(HttpContext context)
		{
			var form = context.Request.Form;
			var problem = form["problem"];
			var repro = form["repro"];
			var email = form["email"];

			if (!String.IsNullOrEmpty(problem) && !String.IsNullOrEmpty(repro))
			{
				using var connection = Database.OpenNewConnection();

				bool success = await AddBugReport(problem, repro, email, connection, context);
				await context.Response.Body.WriteAsync(Utf8Json.JsonSerializer.SerializeUnsafe(new {success}));
			}
			else
			{
				await CommonController.WriteError(context, "This request is missing data" ,StatusCodes.Status400BadRequest);
			}
		}

		private static async Task<bool> AddBugReport(string problem, string repro, string email, NpgsqlConnection connection, HttpContext context)
		{
			try
			{
                int success = await Database.ExecuteAsync(connection,@"insert into bug_reports (problem, repro, email) values (@problem, @repro, @email)", context, new { problem, repro, email });
                return success > 0;
			}
			catch (Exception e)
			{
				return false;
			}
		}
	}
}