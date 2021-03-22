using System.Threading.Tasks;
using Microsoft.AspNetCore.Http;
using Utf8Json;

namespace VivistaServer
{
	public class PingController
	{
		[Route("GET", "/api/ping")]
		[Route("GET", "/api/v1/ping")]
		private static async Task Ping(HttpContext context)
		{
			await context.Response.Body.WriteAsync(Utf8Json.JsonSerializer.SerializeUnsafe("{alive: true}"));
		}
	}
}