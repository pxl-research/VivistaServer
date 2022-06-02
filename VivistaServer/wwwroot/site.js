window.onload = function () {
	InitDarkModeToggles();
	InitPlayButton();
	InitSearch();
	InitTagInput();
	InitUser();
	CheckCookieConsent();
	InitDetailPlaylist();
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
	var buttons = document.getElementsByClassName("download-button");
	if (buttons != undefined) {
		for (const button of buttons) {
			if (button != undefined) {
				button.addEventListener("click", function (event) {
					var message = button.getElementsByClassName("install-message")[0];
					message.classList.remove("hidden");

					if (!event.target.classList.contains("install-download-anchor")) {
						window.location = button.dataset.uri;
						console.log(button.dataset.uri)
					}
				});
			}
		}
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
			tag.addEventListener("click", function () { OnTagRemove(tag) });
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
		newTag.addEventListener("click", function () { OnTagRemove(newTag) });

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


function InitUser() {
	let videoButton = document.getElementById("videos-button");
	if (videoButton != undefined) {
		videoButton.addEventListener("click", function () {
			if (!this.classList.contains("pure-button-active")) {
				this.classList.add("pure-button-active");
				document.getElementById("playlists-button").classList.remove("pure-button-active");
				document.getElementById("videos-userpage").classList.remove("hidden");
				document.getElementById("playlists-userpage").classList.add("hidden");
				document.getElementById("size-videos").classList.remove("hidden");
				document.getElementById("size-playlists").classList.add("hidden");
			}
		});
	}

	let playlistButton = document.getElementById("playlists-button")

	if (playlistButton != undefined) {
		playlistButton.addEventListener("click", function () {
			if (!this.classList.contains("pure-button-active")) {
				this.classList.add("pure-button-active");
				document.getElementById("videos-button").classList.remove("pure-button-active");
				document.getElementById("videos-userpage").classList.add("hidden");
				document.getElementById("playlists-userpage").classList.remove("hidden");
				document.getElementById("size-videos").classList.add("hidden");
				document.getElementById("size-playlists").classList.remove("hidden");
			}
		});

	}
}

const draggables = document.querySelectorAll('.draggable');
const container = document.querySelectorAll('.draggable-container')[0];
let playlistid = new URLSearchParams(window.location.search).get('id');

function InitDetailPlaylist() {
	let indexDrag;

	if (draggables != undefined) {
		draggables.forEach(draggable => {
			draggable.addEventListener('dragstart', () => {
				draggable.classList.add('dragging');

				//Note: Get index of draggedObject
				let videoIds = [];
				document.querySelectorAll('.draggable').forEach(element => {
					videoIds.push(element.id);
				});
				indexDrag = videoIds.indexOf(draggable.id);
			})
		});

		draggables.forEach(draggable => {
			draggable.addEventListener('dragend', () => {
				draggable.classList.remove('dragging');
				let newOrderDragables = document.querySelectorAll('.draggable');
				let videoIds = [];
				newOrderDragables.forEach(element => {
					videoIds.push(element.id);
				});

				let video1 = draggable.id;
				let index = videoIds.indexOf(draggable.id);
				let video2;
				if (indexDrag > index) {
					video2 = videoIds[index + 1];
				}
				else {
					video2 = videoIds[index - 1];
				}

				//Note: Check if there something is changed
				if (video1 != video2 && video2 != undefined) {
					fetch('/api/edit_playlist_order?playlistid=' + playlistid + "&video1=" + video1 + "&video2=" + video2, {
						method: 'POST',
						headers: {
							'Content-Type': 'application/json',
						},
					})
						.then(response => response.json())
						.then(message => {
						})
						.catch((error) => {
						});
				}
			})
		});

	}


	if (container != undefined) {
		container.addEventListener('dragover', e => {
			const afterElement = getDragAfterElement(container, e.clientY);
			const draggable = document.querySelector('.dragging');
			if (afterElement == null) {
				container.appendChild(draggable);
			}
			else {
				container.insertBefore(draggable, afterElement);
			}
		})
	}
}

function getDragAfterElement(container, y) {
	//Note(Tom): Every draggable that we are not currently dragging
	const draggableElements = [...container.querySelectorAll('.draggable:not(.dragging)')];

	//Note(Tom): check the closest element
	return draggableElements.reduce((closest, child) => {
		const box = child.getBoundingClientRect();
		const offset = y - box.top - box.height / 2;
		if (offset < 0 && offset > closest.offset) {
			return { offset: offset, element: child }
		}
		else {
			return closest;
		}
	}, { offset: Number.NEGATIVE_INFINITY }).element;
}