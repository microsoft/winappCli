---
name: local-llm-chat
description: "Build a WinUI 3 app for local ONNX LLM inference with EP selection and chat UI"
type: new
app_name: LocalLLMChat
test_assets:
  - name: "qwen2.5-7b-instruct-qnn-npu-2"
    path: '%USERPROFILE%\.foundry\cache\models\Microsoft\qwen2.5-7b-instruct-qnn-npu-2\v2'
    include_in_build: true
    description: "Use this as the default model path in the app so it can be tested immediately. Test all available execution providers supported on current device"
requirements:
  - "A text field or file picker must allow the user to specify the path to an ONNX LLM model file"
  - "The app must enumerate available ONNX Runtime execution providers (e.g. CPU, DirectML, CUDA) for the local device and show them in a dropdown or list"
  - "A selectable control (ComboBox or RadioButtons) must let the user choose which execution provider to use"
  - "A Load Model button must initiate model loading with visible progress indication (ProgressBar or ProgressRing)"
  - "A chat interface must display messages in a scrollable list with clear visual distinction between user and assistant messages"
  - "A text input box and Send button must allow the user to submit prompts to the loaded model"
  - "Model responses should stream tokens as they are generated (not wait for the full response)"
  - "A Clear or New Chat button must reset the conversation history"
  - "The app must successfully load the test model and respond to a simple prompt like 'Hello'"
  - "The model runs on the execution provider the user selected, and providers that are unavailable on the device are shown as unavailable"
---

Create a new app which implements a chat interface between the user and a GenAI model running locally. Allow the user to provide the path to the ONNX model, allow an option for Execution provider selection that is supported on the device, use the Microsoft.Windows.AI.MachineLearning for Execution Provider discovery and selection, use the Microsoft.ML.OnnxRuntimeGenAI APIs for model inference with the local model.

