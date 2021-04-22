window.onload = function () {
	InitDarkModeToggles();
}

function UpdateDarkModeToggles() {
	var els = document.getElementsByClassName("dark-mode-toggle");
	for (var i = 0; i < els.length; i++) {
		els[i].checked = document.documentElement.dataset.theme == "dark"
	}
}

function InitDarkModeToggles()
{
	var data = document.documentElement.dataset;
	data.theme = localStorage.getItem("theme");

	if (data.theme === null) {
		data.theme = document.documentElement.dataset;
	}
	UpdateDarkModeToggles();

	var els = document.getElementsByClassName("dark-mode-toggle");
	for (var i = 0; i < els.length; i++) {
		let el = els[i];
		el.addEventListener("change", function () {
			data.theme = el.checked ? "dark" : "";
			localStorage.setItem("theme", data.theme);
			UpdateDarkModeToggles();
		});
	};
}
