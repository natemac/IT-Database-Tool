IT DATABASE BUILDER v1.2.0

GET STARTED
1. Extract the entire ZIP into a folder on your computer.
2. Double-click IT Database Builder.exe.
   The app checks PostgreSQL and shows your database library.
3. If PostgreSQL is missing, use Help > PostgreSQL / pgAdmin setup.
4. Use Add files to choose files or Auto-Find them in your games folder.
5. Check the games you want and click Install selected.
Help > Welcome / basics shows the app version, GitHub link and startup preference.

INSTALL POSTGRESQL IF NEEDED
The tool includes PostgreSQL 8.3 and pgAdmin III in:
installs\PostgreSQL v8.3
Use Help > PostgreSQL / pgAdmin setup for the guided steps.
Read Introduction, then use Open Services on the Services page.
Set Secondary Logon to Automatic and start it as an administrator.
On Install, read the steps and click Run setup for the included SETUP.bat.
Follow the service-account, database-cluster and final installer settings.
On Finish & restart, enter your database password. Finish saves it and connects.
Restart Windows yourself. Reopen this app and click Connect.
If installer files are missing, the Install page offers a download link.
For a fresh reinstall, read the Introduction before removing PostgreSQL.
Info courtesy of Eggmansworld: https://github.com/Eggmansworld/tp-it-guides

YOUR DATABASE LIBRARY
Checkboxes control Install selected and Selected actions.
Ctrl/Shift-click rows; batch actions enable only when every highlighted game qualifies.
Properties shows backup history; right-click a version to Restore or Delete it.
Select all checks every game, including installed games and missing files.
Click it again to clear every checkbox; Library also offers Clear checkboxes.
Refresh checks databases and files again, keeping highlights and checkboxes.

ADD YOUR DATABASE FILES
Add files > Auto-Find searches every subfolder of the folder you choose.
It remembers original files without copying, moving, renaming or deleting them.
PG_backup starts empty. Extract ZIP or RAR packs first.
Unclear matches need a manual choice; Latest report shows the details.
Right-click one game > Choose database file to select its source archive.
Red paths show the expected file in pg_backup when it is missing.
Auto-Find skips personal backups; select one explicitly when you want to restore.

INSTALL, CLEAN INSTALL OR REMOVE
Install selected adds missing databases after their files restore successfully.
Selected actions uses checkboxes; Database actions uses highlighted rows.
Clean install uses the Install source shown in Properties, not the latest-backup column.
It asks first and keeps the previous database for recovery.
Restore errors or a database in use leave the original unchanged.
Remove deletes installed databases, including saved progress and scores.
It asks first. Original archive files, personal backups and recovery copies are kept.
Close games and pgAdmin connections before clean installing or removing databases.

BACK UP YOUR DATABASES
Backup databases saves ALL installed games, regardless of their checkboxes.
Use Selected actions > Back up selected databases to back up checked games only.
Right-click > Back Up backs up the highlighted installed games; counts are shown.
Library > Open backups folder opens pg_backup.
Files use YYYY-MM-DD/GameName_YYMMDD_DBbackup; kept versions add _2, _3.
First install saves an Initial database; the file column shows the newest backup.
If a backup exists, choose Overwrite latest, Keep versions, or Cancel.
Backups are verified; failures keep prior files. Backed up shows Never or a date.
A date is historical: later progress is not included; Properties flags missing files.
UserSettings.json records paths, dates, encoding, size and SHA-256.
Right-click a game > Restore backup to choose a version, including Initial database.

ADD OR EDIT A GAME
Use Library > Add game, or right-click a game > Edit game.
DatabaseInfo.json holds games and source archive associations.
UserSettings.json keeps connection, password, preferences and backup history locally.
Keep supplied encoding settings for original game files; tracked backups use theirs.
The app fixes the known IT category key mismatch when restoring original archives.
Password starts blank; enter yours. Edits save, and old settings migrate automatically.

CHECK AND SHARE
Green: Installed. Black: Ready. Red: Missing file or Needs repair.
Needs repair means no game tables or a recorded failure for that exact database.
Use Latest report to review errors. Failed installs leave the game database absent.
Share IT-Database-Setup.zip; it excludes settings, backups and reports.
Updates preserve settings. Keep your database list if customized.
Each user supplies their own game database files.
