IT DATABASE BUILDER

GET STARTED
1. Extract the entire ZIP into a folder on your computer.
2. Double-click IT Database Builder.exe.
   The app checks PostgreSQL and shows which databases are installed.
3. If PostgreSQL is missing, follow the guided setup window in this app.
4. Add your extracted game database files.
5. Check the games you want and click Install.
Welcome / basics explains the app. Use Don't show again to hide it at startup.

INSTALL POSTGRESQL IF NEEDED
The tool includes PostgreSQL 8.3 and pgAdmin III in:
installs\PostgreSQL v8.3

Click PostgreSQL / pgAdmin setup in the app.
Read Introduction, then use Open Services on the Services page.
Set Secondary Logon to Automatic and start it as an administrator.
On Install, read the steps and click Run setup for the included SETUP.bat.
Follow the service-account, database-cluster and final installer settings.
On Finish & restart, enter your database password. Finish saves it and connects.
Restart Windows yourself. Reopen this app and click Connect.
If installer files are missing, the Install page offers a download link.
For a fresh reinstall, read the Introduction before removing PostgreSQL.
pgAdmin does not need to be open when building databases.
Info courtesy of Eggmansworld: https://github.com/Eggmansworld/tp-it-guides

ADD YOUR DATABASE FILES
Click Auto-Find database files and choose your games folder, wherever it is.
The app searches every subfolder and remembers matching files where they are.
Names can vary, such as Golden Tee 2018 or golden_tee_live_2018.
Unclear matches need a manual choice; Open latest report shows the details.
PG_backup starts empty; add your files or use Auto-Find database files.
Loose files and subfolders both work inside PG_backup.
Extract ZIP or RAR packs first.

Red paths show the expected database file inside pg_backup when it is missing.
Click that path, or select the game and click Add database file, to choose a file.
The app remembers its original location without copying, moving or renaming it.
Original files are left untouched. If matching is unclear, choose the file.

INSTALL ALL OR JUST A FEW GAMES
Ready databases start checked; installed and unavailable databases start unchecked.
Select all checks every game, including installed games and missing files.
Click it again to clear every checkbox. Missing files are skipped during install.
Click a game row to check it; the file/path column opens the file picker.
Install: Adds missing databases only after their files restore successfully.
Clean install: Restores installed databases from their original files.
It asks first and keeps the previous database for recovery.
Restore errors or a database in use leave the original unchanged.
Remove: Deletes checked installed databases, including saved progress and scores.
It asks first. Original database files and recovery copies are kept.
Close games and pgAdmin connections before clean installing or removing databases.
Reload checks databases and files again. Password changes keep your selections.

ADD OR EDIT A GAME
Use Add game or select a row and click Edit game.
Changes are saved in DatabaseInfo.json. Use Choose file to select its database file.
DatabaseInfo.json contains the database list and selected archive locations.
UserSettings.json keeps your connection, password and welcome preference locally.
Keep the supplied encoding settings when using original game database files.
The app detects and fixes the known IT category key mismatch during restore.
Password starts blank. Enter your PostgreSQL password; edits save automatically.
The saved password fills the editable field next time you open the app.
Older connection settings move automatically; existing user settings take priority.

CHECK THE RESULTS
Green: Installed. Green with an original file: Installed or clean install.
Black: Ready to install. Red: Missing database file or Needs repair.
Needs repair means no game tables or a recorded failure for that exact database.
Imported means the restore completed without reported errors.
If a game says review errors or Needs review, click Open latest report.
Failed installs leave the game database absent; correct the error and retry Install.
Use Clean install to repair it, or Remove and then Install to start over.

SHARE THE TOOL
Share IT-Database-Setup.zip. It excludes UserSettings.json and starts passwords blank.
Updates preserve your settings; keep your database list if you customized it.
Each user supplies their own game database files. Files and reports are not included.
