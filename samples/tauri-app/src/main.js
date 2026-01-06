const { invoke } = window.__TAURI__.core;

let greetInputEl;
let greetMsgEl;
let pfnMsgEl;
let notifyBtnEl;

async function greet() {
  // Learn more about Tauri commands at https://tauri.app/develop/calling-rust/
  greetMsgEl.textContent = await invoke("greet", { name: greetInputEl.value });
}

async function checkPackageIdentity() {
  const pfn = await invoke("get_package_family_name");

  if (pfn !== "No package identity" && pfn !== "Not running on Windows" && !pfn.startsWith("Error")) {
    notifyBtnEl.disabled = false;
    pfnMsgEl.textContent = `Package family name: ${pfn}`;
  } else {
    notifyBtnEl.disabled = true;
    pfnMsgEl.textContent = `Not running with package identity`;
  }
}

async function sendNotification() {
  try {
    await invoke("show_notification", { title: "Tauri App", body: "Hello from Windows Notification!" });
  } catch (e) {
    console.error(e);
    alert("Failed to send notification: " + e);
  }
}

window.addEventListener("DOMContentLoaded", () => {
  greetInputEl = document.querySelector("#greet-input");
  greetMsgEl = document.querySelector("#greet-msg");
  pfnMsgEl = document.querySelector("#pfn-msg");
  notifyBtnEl = document.querySelector("#notify-btn");

  document.querySelector("#greet-form").addEventListener("submit", (e) => {
    e.preventDefault();
    greet();
  });

  notifyBtnEl.addEventListener("click", () => {
    sendNotification();
  });

  checkPackageIdentity();
});
