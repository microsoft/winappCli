---
name: calc
description: "Standard-mode calculator app — basic arithmetic, decimal entry, keyboard input, and history"
type: new
app_name: CalcApp
requirements:
  - "Core arithmetic is correct: digit and decimal entry, chained + − × ÷, =, ± sign toggle, C to clear and ⌫ to delete the last character (e.g. 12.5 × 2 = 25; 7 − 10 = −3; typing 9, ⌫, 6 shows 6)"
  - "Division by zero shows a message (e.g. 'Cannot divide by zero') instead of crashing or showing Infinity/NaN"
  - "Keyboard input works: digits, '.', '+', '-', '*', '/', Enter or '=' to evaluate, Backspace to delete, Escape to clear"
  - "A history pane lists each completed calculation as 'expression = result'"
---

Build a calculator app for Windows in WinUI 3 with Fluent Design. It should look and feel like a clean, modern desktop calculator — large display at the top, grid of buttons below, and a history pane. Support standard arithmetic (+, −, ×, ÷), decimals, sign toggle, clear, and backspace. Keyboard input should work naturally. Handle division by zero gracefully. Keep it focused on the standard calculator mode — no scientific functions needed.
