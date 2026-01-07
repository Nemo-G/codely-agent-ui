const { invoke } = window.__TAURI__.core;

let greetInputEl;
let greetMsgEl;
let unityPingMsgEl;

async function greet() {
  // Learn more about Tauri commands at https://tauri.app/develop/calling-rust/
  greetMsgEl.textContent = await invoke("greet", { name: greetInputEl.value });
}

async function unityPing() {
  try {
    const res = await invoke("unity_ping");
    unityPingMsgEl.textContent = JSON.stringify(res, null, 2);
  } catch (e) {
    unityPingMsgEl.textContent = `Ping failed: ${e}`;
  }
}

window.addEventListener("DOMContentLoaded", () => {
  greetInputEl = document.querySelector("#greet-input");
  greetMsgEl = document.querySelector("#greet-msg");
  unityPingMsgEl = document.querySelector("#unity-ping-msg");
  document.querySelector("#greet-form").addEventListener("submit", (e) => {
    e.preventDefault();
    greet();
  });

  document.querySelector("#unity-ping").addEventListener("click", () => {
    unityPing();
  });
});
