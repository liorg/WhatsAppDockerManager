```bash
http://localhost:5000/swagger/index.html
```
Troubleshooting
הcontainer לא עולה

```bash
docker pull liorgr/whatsapp-single:latest
```

```bash
cd /opt/myapp
 sudo ./update.sh
```

```bash
docker ps -a --format "table {{.Names}}\t{{.Status}}\t{{.Image}}"
```
```bash
docker stop  whatsapp_972504476645_3beff8fa
docker rm whatsapp_972504476645_3beff8fa
```


```bash
sudo systemctl restart whatsapp-manager.service
```

clear
```bash
 docker image prune -a
```


direction=true = from contact to phone
```bash
select * from messages order by sent_at desc limit 5
```







# בדוק logs

docker logs whatsapp_<phone_number>

```bash
# בדוק סטטוס
curl http://localhost:5000/api/phones
```

בעיות Docker socket

# Linux - ודא הרשאות
```bash
sudo chmod 666 /var/run/docker.sock
```
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
```bash
docker ps -a ls /opt/whatsapp-data/
── צור תיקייה עם הרשאות נכונות

sudo mkdir -p /opt/whatsapp-data sudo chown $USER:$USER /opt/whatsapp-data sudo chmod 755 /opt/whatsapp-data
```
── הרשאות לתת-תיקיות שנוצרות דינמית
הוסף את המשתמש לקבוצת docker

```bash
sudo usermod -aG docker $USER
```

── systemd service (פרודקשן)

```bash
sudo tee /etc/systemd/system/whatsapp-manager.service << 'EOF' [Unit] Description=WhatsApp Docker Manager After=network.target docker.service Requires=docker.service

[Service] Type=simple User=lior WorkingDirectory=/home/lior/projects/github/WhatsAppDockerManager/src/WhatsAppDockerManager ExecStart=/usr/bin/dotnet run --configuration Release Restart=always RestartSec=10 Environment=ASPNETCORE_ENVIRONMENT=Production Environment=SUPABASE_URL=your_url Environment=SUPABASE_KEY=your_key
```


[Install] WantedBy=multi-user.target EOF
```bash
sudo systemctl daemon-reload sudo systemctl enable whatsapp-manager sudo systemctl start whatsapp-manager
── בדיקה
```

```bash
docker ps

sudo systemctl status whatsapp-manager journalctl -u whatsapp-manager -f journalctl -u whatsapp-manager -n 50

journalctl -u whatsapp-manager.service -f --no-pager | grep "MSG-RAW"

journalctl -u whatsapp-manager.service -f --no-pager | grep -E "MSG-RAW|MSG]|LID|Contact|PING|error|Error"

journalctl -u whatsapp-manager.service -f --no-pager | grep -E "MSG]|ping_sender|Saved message|Created new|Found existing|matched via|LID-JID"

journalctl -u whatsapp-manager.service -f --no-pager | grep -E "MSG-RAW|Duplicate|Error|error|Exception" docker logs whatsapp_972-XXXXXXX --tail 50

docker inspect whatsapp_9725xxxxx| grep -A 20 Mounts
```

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
bash```
docker exec whatsapp_972504476645_3beff8fa \
  cp /app/baileys/src/index.js /app/baileys/src/index.js.bak33
'''
# העתקה + אימות תחביר לפני restart
bash```
docker cp ~/index.js whatsapp_972504476645_3beff8fa:/app/baileys/src/index.js
docker exec whatsapp_972504476645_3beff8fa node --check /app/baileys/src/index.js && echo SYNTAX_OK
```

# רק אם SYNTAX_OK
bash```
docker restart whatsapp_972504476645_3beff8fa
sleep 15
curl -m 5 localhost:9369/version || docker logs --tail 30 whatsapp_972504476645_3beff8fa
```




כמעט — אבל בסדר הפוך. קודם tail -f, ורק אחר כך restart.

אם תעשה restart קודם, תשחרר את התור ותאבד את הראיה. הרגע התקוע הוא הרגע היקר.

הסדר בפעם הבאה:


# 1. תצפית ראשונה — לפני שנוגעים בכלום
bash```
docker exec whatsapp_972504476645_3beff8fa tail -f /var/log/baileys.log | grep -E "recv xml|UPSERT|decrypt|SOCK"
```

# 2. תשלח הודעה מהטלפון ותראה מה מופיע
מה רואים	מסקנה	פעולה
רק ping/pong	וואטסאפ לא מנתב	restart — ותדע שזה הפתרון היחיד
<message> בלי UPSERT fired	Baileys בולע	אל תעשה restart — תסתכל על decrypt
UPSERT fired + FAILED	Redis/Spine	הבעיה לא כאן בכלל

רק אחרי שראית — restart.

ושווה לצלם שני נתונים לפני ה-restart, הם לא ישרדו:

bash```
docker exec whatsapp_972504476645_3beff8fa ss -tin 'dport = :443' | grep -oE "sport|bytes_received:[0-9]*|lastrcv:[0-9]*"
docker exec whatsapp_972504476645_3beff8fa awk '/new socket created/{n=0} /Timed Out/{n++} END{print n}' /var/log/baileys.log
```


וטיפ חשוב לפני restart: נסה קודם לשלוח הודעה יוצאת:

bash```
curl -X POST localhost:9369/send/text -H 'Content-Type: application/json' \
  -d '{"jid":"972546252491","text":"wake"}'
  ```


אם התור משתחרר מיד אחרי — יש לך עקיפה שלא דורשת restart בכלל, ואפשר להפוך אותה לאוטומטית (heartbeat יוצא כל X דקות). זה שווה הרבה יותר מ-restart ידני.

### delete phone


bash```
select container_name from phones where id='8e0b80f9-1534-436f-950d-256783582428'

  ```
delete containers whatsapp_972546252491_8e0b80f9

```bash
docker ps -a --format "table {{.Names}}\t{{.Status}}\t{{.Image}}"
```


```bash
docker stop  whatsapp_972546252491_8e0b80f9
docker rm whatsapp_972546252491_8e0b80f9
```

```bash
DO $$
DECLARE
    v_phone_id uuid := '8e0b80f9-1534-436f-950d-256783582428';
    r RECORD;
BEGIN
    FOR r IN
        SELECT table_schema, table_name
        FROM information_schema.columns
        WHERE column_name = 'phone_id'
          AND table_schema = 'public'
          AND table_name <> 'phones'
    LOOP
        RAISE NOTICE 'Deleting from %.%', r.table_schema, r.table_name;

        EXECUTE format(
            'DELETE FROM %I.%I WHERE phone_id = $1',
            r.table_schema,
            r.table_name
        )
        USING v_phone_id;
    END LOOP;

    -- phones נמחקת אחרונה
    DELETE FROM public.phones
    WHERE id = v_phone_id;

END $$;
```
