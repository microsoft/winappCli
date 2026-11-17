# Python package identity

This console sample calls `Windows.ApplicationModel.Package` to compare ordinary execution with Windows package identity.

Follow the [Python prerequisites](../../docs/guides/python.md#prerequisites) and enable **Windows Developer Mode** before the identity run. For a GUI, see the [WinUI sample](../python-winui).

## 1. Initialize

```powershell
cd .\samples\python-identity
winapp init --use-defaults --add-python-bindings
```

## 2. Run with development identity

```powershell
winapp run --identity .\app.py
```

Expected output: `Package Family Name: your-project.debug_publisherhash`.

Use `winapp run --help` for interpreter selection and application dependency options.

## 3. Clear development identity

```powershell
winapp identity clear
```

This removes the development registration and private environment while keeping the generated bindings. See [cleanup behavior](../../docs/guides/python.md#6-clean-up-development-identity) for recovery and preserved data.

## 4. Compare without identity

The identity steps above do not require an application `.venv`. To compare, run the same script with ordinary Python:

```powershell
python -m venv .venv
.\.venv\Scripts\python.exe -m pip install -r .\requirements.txt
.\.venv\Scripts\python.exe .\app.py
```

Expected output: `Not packaged`, because this interpreter has no Windows package identity. Skip environment creation if `.venv` already exists.

## 5. Package as MSIX

Follow the [Python guide's MSIX workflow](../../docs/guides/python.md#7-package-as-msix). This sample already includes the `requirements-dev.txt` used by that workflow.

## Test

From the repository root, with Pester 5 and a built Python wheel:

```powershell
.\scripts\test-samples.ps1 -Samples python-identity -WinappPath .\artifacts\python
```
