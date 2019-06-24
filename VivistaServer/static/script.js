window.onload = function()
{
	var questions = document.getElementsByClassName("question");
	var answers = document.getElementsByClassName("answer");
	for (var a of answers)
	{
		a.classList.add("hidden");
	}

	for (var q of questions)
	{
		q.addEventListener("click", function()
		{
			var answer = this.nextElementSibling;
			var active = this.classList.contains("active");
			if (active)
			{
				this.classList.remove("active");
				answer.classList.add("hidden");
			}
			else
			{
				this.classList.add("active");
				answer.classList.remove("hidden");
			}
		});
	}
};