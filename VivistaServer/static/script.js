window.onload = function()
{
	//Contactform validation 
	var form = document.getElementById("contact_form");
	if(form != null)
	{
		form.onsubmit = function(){validate_form(); return false;};
		var inputs = form.getElementsByTagName("input");
		for(var input of inputs)
		{
			input.addEventListener('input', checklines);
		}
	}

	// FAQ : check for the questions and answers. 
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

	wrapDivAroundImage();
};

// check the lines in the contactform
function checklines()
{
	var fname = document.getElementById("fname");
	var lname = document.getElementById("lname");
	var subject = document.getElementById("subject")

	if (document.getElementById("fname").value != null)
	{
		document.getElementById("errorfname").style.visibility = "hidden";
		valid = true;
	}
	if (document.getElementById("lname").value != null)
	{
		document.getElementById("errorlname").style.visibility = "hidden";
		valid = true;
	}

	if(document.getElementById("subject").value != null)
	{
		document.getElementById("errorsubject").style.visibility = "hidden";
		valid = true;
	}
	
}

// check if the input in the email form is true else it is false and give red lines
function validate_form()
{
	var fname = document.getElementById("fname");
	var lname = document.getElementById("lname");
	var subject = document.getElementById("subject")

	valid = true;

	if (document.getElementById("fname").value == "")
	{
		document.getElementById("errorfname").style.visibility = "visible";
		
		valid = false;
	}
	else
	{
		document.getElementById("errorfname").style.visibility = "hidden";
		valid = true;
	}
	if (document.getElementById("lname").value == "")
	{
		document.getElementById("errorlname").style.visibility = "visible";
		valid = false;
	}
	else
	{
		document.getElementById("errorlname").style.visibility = "hidden";
		valid = true;
	}

	if (document.getElementById("subject").value == "")
	{
		document.getElementById("errorsubject").style.visibility = "visible";
		valid = false;
	}
	else
	{
		document.getElementById("errorsubject").style.visibility = "hidden";
		valid = true;
	}

	// Transfers the firstname, lastname and subject to an email. The escape ("\n" gives the email an enter)
	if(document.getElementById("fname").value != "" && document.getElementById("lname").value != ""  && 
		document.getElementById("subject").value != "")
	{
		window.location.href="mailto:baetenjoey@hotmail.com?Subject=Website Vivista &body=" + escape("\n")
		+(document.getElementById("subject").value) + escape("\n\n"); 
	}
	return false;
}

// wraps the images that are given the classname img-hover-zom around with a div 
function wrapDivAroundImage(){

	var elements = Array.from(document.getElementsByClassName('img-hover-zoom'));

	for(var i = 0; i < elements.length; i++)
	{
		//create wrapper container
		var wrapper = document.createElement('div');
		wrapper.setAttribute("class",'img-hover-zoom');

		//insert wrapper before el in the DOM tree
		elements[i].parentNode.insertBefore(wrapper, elements[i]);

		//mover el into wrapper
		wrapper.appendChild(elements[i]);
	}
}





