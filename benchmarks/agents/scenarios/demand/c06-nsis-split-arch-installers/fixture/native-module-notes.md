ScoutDesk includes `better-sqlite3`.

The release job builds both architectures from one working tree. QA unpacked the combined installer and found only one `better_sqlite3.node` copy under the app resources. We need separate installers so support can hand the x64 file to x64 users and the arm64 file to Snapdragon users.
