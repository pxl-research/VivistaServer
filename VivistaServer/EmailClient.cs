using System;
using System.Net;
using System.Threading.Tasks;
using MailKit.Net.Smtp;
using MimeKit;

namespace VivistaServer
{
	public class EmailClient
	{
		private static MailboxAddress noreplyAddress = new MailboxAddress("Vivista", "noreply@vivista.net");
		private static string SMTPServerAddress = "in-v3.mailjet.com";

		private static string SMTPUsername;
		private static string SMTPPassword;

		public static async Task<bool> SendPasswordResetMail(string receiver, string token)
		{
			var url = $"{CommonController.baseURL}/reset_password_finish?email={receiver}&token={WebUtility.UrlEncode(token)}";

			var message = new MimeMessage();
			message.From.Add(noreplyAddress);
			message.To.Add(new MailboxAddress(receiver, receiver));
			message.Subject = "Vivista password reset code";

			var builder = new BodyBuilder();

			builder.HtmlBody = $"<p>A password reset has been requested for {receiver}. \n Click or copy and paste the following link to reset your password. \n\n <a href=\"{ url }\">{url}</a> \n\n If you did not request a password reset, you can safely ignore this email. \n The password reset link will remain valid for one hour.</p>";
			message.Body = builder.ToMessageBody();

			return await SendMail(message);
		}

		public static async Task<bool> SendEmailConfirmationMail(string receiver, string token)
		{
			var url = $"{CommonController.baseURL}/verify_email?email={WebUtility.UrlEncode(receiver)}&token={WebUtility.UrlEncode(token)}";

			var message = new MimeMessage();
			message.From.Add(noreplyAddress);
			message.To.Add(new MailboxAddress(receiver, receiver));
			message.Subject = "Confirm email address";

			var builder = new BodyBuilder();

			builder.HtmlBody = $"<p>Thank you for creating an account on Vivista! \n Click or copy and paste the following link to verify your email address. \n\n <a href=\"{ url }\">{url}</a></p>";

			message.Body = builder.ToMessageBody();

			return await SendMail(message);
		}

		private static async Task<bool> SendMail(MimeMessage message)
		{
			try
			{
				using var client = new SmtpClient();
				await client.ConnectAsync(SMTPServerAddress, 587);
				await client.AuthenticateAsync(SMTPUsername, SMTPPassword);
				await client.SendAsync(message);
				await client.DisconnectAsync(true);
			}
			catch
			{
				return false;
			}

			return true;
		}

		public static void InitCredentials()
		{
			if (string.IsNullOrEmpty(SMTPUsername))
			{
				SMTPUsername = Environment.GetEnvironmentVariable("VIVISTA_SMTP_USERNAME");
			}

			if (string.IsNullOrEmpty(SMTPPassword))
			{
				SMTPPassword = Environment.GetEnvironmentVariable("VIVISTA_SMTP_PASSWORD");
			}

			if (string.IsNullOrEmpty(SMTPUsername) || string.IsNullOrEmpty(SMTPPassword))
			{
				Console.WriteLine("Couldn't find VIVISTA_SMTP_USERNAME and/or VIVISTA_SMTP_PASSWORD environment variables");
				throw new Exception("Couldn't find VIVISTA_SMTP_USERNAME and/or VIVISTA_SMTP_PASSWORD environment variables");
			}
		}
	}
}