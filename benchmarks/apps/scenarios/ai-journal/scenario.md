---
name: ai-journal
description: "Implicitly tests the usage of local Windows Copilot Runtime / Generative AI / Phi Silica APIs."
type: new
app_name: SecureJournal
requirements:
  - "The app must contain a text area for writing entries and a button to trigger 'Insights'."
  - "Insights and semantic search work entirely on the device: no network requests are made while they run (cloud APIs such as OpenAI/Azure are failures)"
  - "The UI must display a ProgressRing or loading state while the local model is generating the summary."
  - "The UI thread must remain responsive during the summarization process."
  - "The summary output must be formatted as a bulleted list."
  - "The app must have a way to save and load journal entries."
  - "The app must have a way to delete journal entries."
  - "The app must have a way to search journal entries with semantic search."
  - "The app must allow the user to start a new journal entry from a quick task in the taskbar"
  - "The app must allow other apps to share content to it for starting a new journal entry."
  - "The app must handle when an ai is not available and provide good fallback behavior and messaging."
---

Build a secure, privacy-focused daily journaling application. The app must include an 'Insights' button that reads the user's past 7 days of entries and automatically generates a short, bulleted summary of key themes and action items. It should also allow the user to search journal entries by their meaning and concept using natural language, not just by exact match. It should also create quick commands for new journal entry from the taskbar, and the ability for other apps to share content to the app to create new journal entry. Because this contains highly sensitive personal data, this processing must happen entirely offline on the local device without making any external network requests while also ensuring it is not draining the battery and is efficent.
