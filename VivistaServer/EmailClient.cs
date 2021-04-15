using System;
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
			var url = $"{CommonController.baseURL}/reset_password_finish?token={token}";

			var message = new MimeMessage();
			message.From.Add(noreplyAddress);
			message.To.Add(new MailboxAddress(receiver, receiver));
			message.Subject = "Vivista password reset code";
			message.Body = new TextPart("plain"){Text = $@"A password reset has been requested for {receiver}. 
														Click or copy and paste the following link to reset your password. 

														{url}

														If you did not request a password reset, you can safely ignore this email. 
														The password reset link will remain valid for one hour."};

			return await SendMail(message);
		}

		public static async Task<bool> SendEmailConfirmationMail(string receiver, string token)
		{
			var url = $"{CommonController.baseURL}/verify_email?email={receiver}&token={token}";

			var message = new MimeMessage();
			message.From.Add(noreplyAddress);
			message.To.Add(new MailboxAddress(receiver, receiver));
			message.Subject = "Confirm email address";
			message.Body = new TextPart("plain") { Text = $@"Thank you for creating an account on Vivista! 
														Click or copy and paste the following link to verify your email address. 

														{url}"};

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