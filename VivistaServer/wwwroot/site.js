window.onload = function () {
	InitDarkModeToggles();
	InitPlayButton();
	InitSearch();
	CheckCookieConsent();
}

function UpdateDarkModeToggles() {
	var els = document.getElementsByClassName("dark-mode-toggle");
	for (var i = 0; i < els.length; i++) {
		els[i].checked = document.documentElement.dataset.theme == "dark"
	}
}

function InitDarkModeToggles() {
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

function InitPlayButton() {
	var button = document.getElementsByClassName("download-button")[0];

	if (button != undefined) {
		button.addEventListener("click", function () {
			var message = document.getElementsByClassName("install-message")[0];
			message.classList.remove("hidden");
			window.location = button.dataset.uri;
		});
	}
}

function InitSearch() {
	var searchInput = document.getElementById("search-input");
	var searchButton = document.getElementById("search-button");

	if (searchInput != undefined && searchButton != undefined) {
		searchButton.addEventListener("click", submit);
		searchInput.addEventListener("keydown", function (e) {
			if (e.keyCode == 13) {
				submit();
			}
		});
	}

	function submit() {
		var text = searchInput.value;
		if (text != undefined && text.length > 0) {
			location.href = "/search?q=" + text;
		}
	}
}

function CheckCookieConsent() {
	if (GetCookie("cookie-consent") != "true") {
		ShowCookieBanner();
		SetCookie("cookie-consent", true, 365);
	}
}

function GetCookie(name) {
	var allCookies = document.cookie;
	if (allCookies.length > 0) {
		var split = allCookies.split("; ");
		for (var i = 0; i < split.length; i++) {
			if (split[i].startsWith(name + "=")) {
				return split[i].split("=")[1];
			}
		}
		return null;
	} else {
		return null;
	}
}

function SetCookie(name, value, days) {
	var expires = "";
	if (days) {
		expires = "; max-age=" + days * 86400;
	}
	document.cookie = name + "=" + (value || "") + expires + "; path=/";
}

function ShowCookieBanner() {
	document.getElementById("cookie-banner").classList.remove("hidden");
	document.getElementById("cookie-banner-confirm").addEventListener("click", HideCookieBanner);
}

function HideCookieBanner() {
	document.getElementById("cookie-banner").classList.add("hidden");
}