```bash
http://localhost:5000/swagger/index.html
```
Troubleshooting
הcontainer לא עולה

```bash
docker pull liorgr/whatsapp-single:latest

docker rm -f whatsapp_972504476645_3beff8fa
sudo systemctl restart whatsapp-manager
docker exec whatsapp_972504476645_3beff8fa printenv REDIS_URL
cd /opt/myapp
 sudo ./update.sh
```

```bash
docker ps -a --format "table {{.Names}}\t{{.Status}}\t{{.Image}}"
```
```bash
docker stop  whatsapp_972504476645_3beff8fa
docker rm whatsapp_972504476645_3beff8fa

sudo systemctl restart whatsapp-manager
docker exec whatsapp_972504476645_3beff8fa printenv REDIS_URL

```bash
sudo systemctl restart whatsapp-manager.service
```

clear
```bash
 docker image prune -a
```

REDIS?

```bash
sudo systemctl restart whatsapp-manager
sleep 20 && docker exec whatsapp_972504476645_3beff8fa printenv REDIS_URL
docker exec redis_shared redis-cli smembers webhooks:3beff8fa-4dc6-4a03-b70f-17a47fe09529

```

TEST BAILIES LOCAL AND HUB

C=whatsapp_972504476645_3beff8fa
```bash
# 1 · הגרסה שהקונטיינר מדווח על עצמו
docker exec $C printenv APP_VERSION
curl -s localhost:8161/health | python3 -m json.tool 2>/dev/null | head -20

# 2 · ה-image שבשימוש מול ה-latest המקומי — צריכים להיות זהים
docker inspect $C --format 'container: {{.Image}}'
docker image inspect liorgr/whatsapp-single:latest --format 'latest:    {{.Id}}'

# 3 · ה-latest המקומי מול ה-Hub — זה אומר אם יש משהו חדש שלא נמשך
docker image inspect liorgr/whatsapp-single:latest --format 'local:  {{index .RepoDigests 0}}'
curl -s "https://hub.docker.com/v2/repositories/liorgr/whatsapp-single/tags/latest" \
  | python3 -c "import json,sys;d=json.load(sys.stdin);print('hub:   ',d['digest'],d['last_updated'])"

```

LOG SEND RECIEVE

```bash

journalctl -u whatsapp-manager -f --no-pager | grep -E "RAW-PAYLOAD|Container event|ERR"
```


עכשיו האימות. מה שרץ בפועל:

```bash
# 1 · איזו גרסה באוויר
curl -s localhost:5000/api/templates/health | python3 -m json.tool

# 2 · שני ה-keys הגיעו לתהליך
sudo tr '\0' '\n' < /proc/$(systemctl show -p MainPID --value whatsapp-manager)/environ | grep AppSettings__

# 3 · הקוד החדש באמת רץ — זו השורה שקיימת רק בו
journalctl -u whatsapp-manager --since "-10min" --no-pager | grep -E "sweep|redis_shared|Redis container|has no ```


```bash
systemctl status whatsapp-manager --no-pager | head -20
journalctl -u whatsapp-manager -n 60 --no-pager | tail -40

```


direction=true = from contact to phone
```bash
select * from messages order by sent_at desc limit 5
```

```bash
curl -s "https://hub.docker.com/v2/repositories/liorgr/whatsapp-cloudapi/tags/?page_size=25" \
  | python3 -c "import json,sys;[print(t['name']) for t in json.load(sys.stdin)['results']]"

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
    v_phone_id uuid := '1ff94cfa-a381-4606-8bbc-f0d36abe8005';
BEGIN

    ------------------------------------------------------------
    -- 1. execution_links
    -- תלוי ב-phone / contact / scenario / schedule
    ------------------------------------------------------------
    DELETE FROM public.execution_links
    WHERE phone_id = v_phone_id
       OR contact_id IN (
            SELECT id FROM public.contacts
            WHERE phone_id = v_phone_id
       )
       OR scenario_id IN (
            SELECT id FROM public.scenarios
            WHERE phone_id = v_phone_id
       )
       OR schedule_id IN (
            SELECT id FROM public.schedules
            WHERE phone_id = v_phone_id
       );


    ------------------------------------------------------------
    -- 2. webhook_messages
    ------------------------------------------------------------
    DELETE FROM public.webhook_messages
    WHERE phone_id = v_phone_id
       OR contact_id IN (
            SELECT id FROM public.contacts
            WHERE phone_id = v_phone_id
       )
       OR call_id IN (
            SELECT id FROM public.calls
            WHERE phone_id = v_phone_id
       );


    ------------------------------------------------------------
    -- 3. ping_sender
    ------------------------------------------------------------
    DELETE FROM public.ping_sender
    WHERE phone_id = v_phone_id
       OR contact_id IN (
            SELECT id FROM public.contacts
            WHERE phone_id = v_phone_id
       );


    ------------------------------------------------------------
    -- 4. messages
    ------------------------------------------------------------
    DELETE FROM public.messages
    WHERE phone_id = v_phone_id
       OR contact_id IN (
            SELECT id FROM public.contacts
            WHERE phone_id = v_phone_id
       )
       OR call_id IN (
            SELECT id FROM public.calls
            WHERE phone_id = v_phone_id
       );


    ------------------------------------------------------------
    -- 5. scenario_runs
    ------------------------------------------------------------
    DELETE FROM public.scenario_runs
    WHERE phone_id = v_phone_id
       OR call_id IN (
            SELECT id FROM public.calls
            WHERE phone_id = v_phone_id
       )
       OR scenario_id IN (
            SELECT id FROM public.scenarios
            WHERE phone_id = v_phone_id
       );


    ------------------------------------------------------------
    -- 6. message_events
    ------------------------------------------------------------
    DELETE FROM public.message_events
    WHERE phone_id = v_phone_id;


    ------------------------------------------------------------
    -- 7. phone_provisioning_events
    ------------------------------------------------------------
    DELETE FROM public.phone_provisioning_events
    WHERE phone_id = v_phone_id;


    ------------------------------------------------------------
    -- 8. calls
    -- messages כבר טופלו
    ------------------------------------------------------------
    DELETE FROM public.calls
    WHERE phone_id = v_phone_id;


    ------------------------------------------------------------
    -- 9. schedules
    ------------------------------------------------------------
    DELETE FROM public.schedules
    WHERE phone_id = v_phone_id;


    ------------------------------------------------------------
    -- 10. scenario self references
    ------------------------------------------------------------
    UPDATE public.scenarios
    SET source_scenario_id = NULL
    WHERE source_scenario_id IN (
        SELECT id
        FROM public.scenarios
        WHERE phone_id = v_phone_id
    );


    ------------------------------------------------------------
    -- 11. scenarios
    ------------------------------------------------------------
    DELETE FROM public.scenarios
    WHERE phone_id = v_phone_id;


    ------------------------------------------------------------
    -- 12. contacts self references
    ------------------------------------------------------------
    UPDATE public.contacts
    SET parent_contact_id = NULL
    WHERE parent_contact_id IN (
        SELECT id
        FROM public.contacts
        WHERE phone_id = v_phone_id
    );


    ------------------------------------------------------------
    -- 13. contacts
    ------------------------------------------------------------
    DELETE FROM public.contacts
    WHERE phone_id = v_phone_id;


    ------------------------------------------------------------
    -- 14. שאר הטבלאות הישירות
    ------------------------------------------------------------
    DELETE FROM public.phone_templates
    WHERE phone_id = v_phone_id;

    DELETE FROM public.sender_log
    WHERE phone_id = v_phone_id;

    -- notifications מוגדר SET NULL,
    -- אבל אם רוצים ניקוי מלא של נתוני הבדיקה נמחק אותן.
    DELETE FROM public.notifications
    WHERE phone_id = v_phone_id;


    ------------------------------------------------------------
    -- 15. phone עצמו — אחרון
    ------------------------------------------------------------
    DELETE FROM public.phones
    WHERE id = v_phone_id;


    RAISE NOTICE 'Phone % and related test data deleted successfully',
        v_phone_id;

END $$;
```

### אין צורך למחוק את הלוגים. מספיק לסמן את נקודת ההתחלה ולקרוא רק ממנה והלאה:

```bash

# 1. נקודת התחלה
START=$(date '+%Y-%m-%d %H:%M:%S'); echo "$START"

# 2. deploy / restart
sudo systemctl restart whatsapp-manager

# 3. מעקב חי אחרי הזמנים
journalctl -u whatsapp-manager --since "$START" -f | grep --line-buffered -E "TIMING|

```


### אם אתה בכל זאת רוצה למחוק לגמרי: הפקודה הבאה מוחקת את כל לוגי ה-journal בשרת, לא רק של ה-Manager, ואין דרך לשחזר אותם:

```bash
sudo journalctl --rotate && sudo journalctl --vacuum-time=1s
```
redis limit

```bash
docker exec redis_shared redis-cli config set maxmemory 384mb
docker exec redis_shared redis-cli config set maxmemory-policy volatile-lru
docker exec redis_shared redis-cli config get maxmemory-policy
```



עכשיו cloudapi
```bash
curl -s -XPOST localhost:5000/api/phones/provision \
  -H 'content-type: application/json' \
  -d '{
    "phoneNumber":      "972507251926",
    "provider":         "cloudapi",
    "wabaId":           "1119020710931381",
    "phoneNumberId":    "<pnid>",
    "cloudAccessToken": "<token>"
  }' | python3 -m json.tool


ו```
```bash
# 3. הקונטיינר
docker rm -f whatsapp_972507251926_61ecde97

```



```bash

# 4. deploy
cd ~/projects/github/WhatsAppDockerManager
sed -i 's|<Version>1.0.234</Version>|<Version>1.0.235</Version>|' src/WhatsAppDockerManager/WhatsAppDockerManager.csproj
git add -A && git commit -m "provision: persist provider + cloud_system_user_id" && git push
# ואחרי ה-CI
cd /opt/myapp && ./update.sh

ו```