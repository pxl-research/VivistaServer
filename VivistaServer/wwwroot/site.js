window.onload = function () {
	InitDarkModeToggles();
	InitPlayButton();
	InitSearch();
	InitTagInput();
	CheckCookieConsent();
}

function UpdateDarkModeToggles() {
	var els = document.getElementsByClassName("dark-mode-toggle");
	for (var i = 0; i < els.length; i++) {
		els[i].checked = document.documentElement.dataset.theme == "dark";
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
		button.addEventListener("click", function (event) {
			var message = document.getElementsByClassName("install-message")[0];
			message.classList.remove("hidden");

			if (!event.target.classList.contains("install-download-anchor")) {
				window.location = button.dataset.uri;
			}
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

function InitTagInput() {
	var inputElement = document.getElementById("tags-input");

	if (inputElement != undefined) {
		var tagHolderElement = document.getElementById("tag-holder");
		var tags = tagHolderElement.getElementsByClassName("tag");

		inputElement.oninput = OnTagInput;
		for (let i = 0; i < tags.length; i++) {
			let tag = tags[i];
			tag.addEventListener("click", function() { OnTagRemove(tag) });
		}
	}
}

var tagSeparatorRegex = new RegExp(/ |,|\n|\t/);
var tagSet = new Set();

function OnTagInput(e) {
	if (e.data != null && e.data.match(tagSeparatorRegex)) {
		var inputElement = document.getElementById("tags-input");
		var parts = inputElement.value.split(tagSeparatorRegex);
		for (var i = 0; i < parts.length; i++) {
			if (parts[i].length > 0) {
				AddTag(parts[i]);
			}
		}

		inputElement.value = "";
	}
}

function AddTag(tag) {
	if (!tagSet.has(tag)) {
		var tagHolderElement = document.getElementById("tag-holder");

		var newTag = document.createElement("span");
		var content = document.createTextNode(tag);

		newTag.classList.add("tag");
		newTag.classList.add("tag-editable");
		newTag.addEventListener("click", function() { OnTagRemove(newTag) });

		newTag.appendChild(content);
		tagHolderElement.append(newTag);
		tagHolderElement.append(document.createTextNode(" "));


		tagSet.add(tag);
		UpdateTagFormValue();
	}
}

function OnTagRemove(element) {
	var tagName = element.textContent;
	element.remove();

	tagSet.delete(tagName);
	UpdateTagFormValue();
}

function UpdateTagFormValue() {
	var tagElement = document.getElementById("tags");

	tagElement.value = Array.from(tagSet).join(",");
}
