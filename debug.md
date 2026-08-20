http://localhost:5000/swagger/index.html
Troubleshooting
הcontainer לא עולה

# בדוק logs
docker logs whatsapp_<phone_number>

# בדוק סטטוס
curl http://localhost:5000/api/phones

בעיות Docker socket

# Linux - ודא הרשאות
sudo chmod 666 /var/run/docker.sock

# או הוסף את המשתמש לקבוצת docker
sudo usermod -aG docker $USER

בעיות חיבור ל-Supabase

# בדוק את ה-URL וה-Key
curl "https://YOUR_PROJECT.supabase.co/rest/v1/hosts" \
  -H "apikey: YOUR_KEY"

פיתוח

# Development mode
cd src/WhatsAppDockerManager
dotnet watch run

# Run tests
dotnet test

# Build for production
dotnet publish -c Release -o ./publish

docker ps -a בדוק אם הם זהים:

md5sum /opt/whatsapp-data/auth_*/creds.json
עצור והסר את כל הקונטיינרים של whatsapp

docker ps -a --filter "label=app=whatsapp-manager" --format "{{.ID}}" | xargs -r docker rm -f
מחק את כל הנתונים

sudo rm -rf /opt/whatsapp-data/*
מחק לוגים של ה-.NET

rm -rf ./logs/*
── עצור והסר כל קונטיינרים של whatsapp

docker ps -a --filter "label=app=whatsapp-manager" --format "{{.ID}}" | xargs -r docker rm -f
── מחק נתונים

sudo rm -rf /opt/whatsapp-data/*
ראה את כל ה-containers הרצים

docker ps --format "table {{.Names}}\t{{.ID}}\t{{.Status}}"
── מחק לוגים

rm -rf ./logs/*
או אחד אחד
── נקה טבלאות Supabase
הרץ ב-Supabase SQL Editor:
בדוק שהכל נקי

docker ps -a ls /opt/whatsapp-data/
── צור תיקייה עם הרשאות נכונות

sudo mkdir -p /opt/whatsapp-data sudo chown $USER:$USER /opt/whatsapp-data sudo chmod 755 /opt/whatsapp-data
── הרשאות לתת-תיקיות שנוצרות דינמית
הוסף את המשתמש לקבוצת docker

sudo usermod -aG docker $USER
── systemd service (פרודקשן)

sudo tee /etc/systemd/system/whatsapp-manager.service << 'EOF' [Unit] Description=WhatsApp Docker Manager After=network.target docker.service Requires=docker.service

[Service] Type=simple User=lior WorkingDirectory=/home/lior/projects/github/WhatsAppDockerManager/src/WhatsAppDockerManager ExecStart=/usr/bin/dotnet run --configuration Release Restart=always RestartSec=10 Environment=ASPNETCORE_ENVIRONMENT=Production Environment=SUPABASE_URL=your_url Environment=SUPABASE_KEY=your_key

[Install] WantedBy=multi-user.target EOF

sudo systemctl daemon-reload sudo systemctl enable whatsapp-manager sudo systemctl start whatsapp-manager
── בדיקה

docker ps

sudo systemctl status whatsapp-manager journalctl -u whatsapp-manager -f journalctl -u whatsapp-manager -n 50

journalctl -u whatsapp-manager.service -f --no-pager | grep "MSG-RAW"

journalctl -u whatsapp-manager.service -f --no-pager | grep -E "MSG-RAW|MSG]|LID|Contact|PING|error|Error"

journalctl -u whatsapp-manager.service -f --no-pager | grep -E "MSG]|ping_sender|Saved message|Created new|Found existing|matched via|LID-JID"

journalctl -u whatsapp-manager.service -f --no-pager | grep -E "MSG-RAW|Duplicate|Error|error|Exception" docker logs whatsapp_972-XXXXXXX --tail 50

docker inspect whatsapp_9725xxxxx| grep -A 20 Mounts
IMAGE

gcloud compute images create whatsapp-server-stable-v1
--source-disk=instance-20260416-115407
--source-disk-zone=europe-central2-c
--description="Stable image with WhatsApp Manager"

gcloud compute addresses create my-static-ip
--addresses=XX.XXX.XXX.XXX
--region=europe-central2

docker pull liorgr/whatsapp-single

docker stop whatsapp_972504476645 docker rm whatsapp_972504476645 systemctl restart whatsapp-manager.service
רישיון

MIT
RUNNING CRONLAB

sudo /opt/myapp/update.sh
GIT

update version WhatsAppDockerManager.csproj git add WhatsAppDockerManager.csproj git add README.md git add Services git add Controllers git status git add Controllers git commit --m 'vers 18' git push





# אימות שהקובץ הגיע שלם
wc -l ~/index.js                    # 856
grep -c currentSock ~/index.js      # 14
grep APP_VERSION ~/index.js | head -1   # 1.0.0.33

# גיבוי
docker exec whatsapp_972504476645_3beff8fa \
  cp /app/baileys/src/index.js /app/baileys/src/index.js.bak33

# העתקה + אימות תחביר לפני restart
docker cp ~/index.js whatsapp_972504476645_3beff8fa:/app/baileys/src/index.js
docker exec whatsapp_972504476645_3beff8fa node --check /app/baileys/src/index.js && echo SYNTAX_OK

# רק אם SYNTAX_OK
docker restart whatsapp_972504476645_3beff8fa
sleep 15
curl -m 5 localhost:9369/version || docker logs --tail 30 whatsapp_972504476645_3beff8fa
