Release requirements for Northstar Shell:

- External customers should be able to install without admin rights.
- Enterprise IT asked whether an all-users installer can be made available separately.
- The app uses file associations and auto-start registration, but no Windows APIs that require package identity.
- Updates are currently handled by the Electron updater service, not by Microsoft Store submission.
