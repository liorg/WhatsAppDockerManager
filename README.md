# WhatsApp Docker Manager

מערכת .NET Core לניהול Docker containers של WhatsApp (מבוסס Baileys) בצורה דינמית.

## תכונות

- ✅ **Cross-Platform** - רץ על Windows, Linux, macOS
- ✅ **ניהול Docker דינמי** - הקמה אוטומטית של containers לפי טבלת טלפונים
- ✅ **HTTP Proxy (YARP)** - העברת בקשות REST לcontainer המתאים
- ✅ **TCP Proxy** - העברת חיבורי WebSocket
- ✅ **High Availability** - כשרת נופל, שרת אחר לוקח את הטלפונים
- ✅ **Health Monitoring** - בדיקות בריאות ואתחול אוטומטי
- ✅ **Supabase Integration** - סנכרון עם מסד נתונים
- ✅ **Webhook Integration** - קבלת events מ-containers

## ארכיטקטורה

```
┌─────────────────────────────────────────────────────────────────────┐
│                           Frontend (React)                          │
│                                                                     │
│   GET /api/routes  →  קבל רשימת טלפונים פעילים                     │
│   GET /wa/972501234567/qrcode/image  →  הצג QR                     │
│   POST /wa/972501234567/send/text  →  שלח הודעה                    │
└────────────────────────────────┬────────────────────────────────────┘
                                 │
                                 ▼
┌─────────────────────────────────────────────────────────────────────┐
│                    WhatsApp Docker Manager (.NET)                   │
│                                                                     │
│  ┌──────────────┐  ┌──────────────┐  ┌──────────────────────────┐  │
│  │  YARP Proxy  │  │  Container   │  │  Background Services     │  │
│  │              │  │  Manager     │  │  - Heartbeat (30s)       │  │
│  │ /wa/{phone}/ │  │              │  │  - Health Check (60s)    │  │
│  │     ↓        │  │  - Create    │  │  - Sync (5min)           │  │
│  │ Container    │  │  - Start     │  │  - Route Sync            │  │
│  │              │  │  - Stop      │  │                          │  │
│  └──────────────┘  └──────────────┘  └──────────────────────────┘  │
│                                                                     │
│  ┌──────────────────────────────────────────────────────────────┐  │
│  │                     Webhook Receiver                          │  │
│  │  POST /api/webhook/container-event/{phoneId}                  │  │
│  │  ← קבלת events מה-containers (messages, auth, disconnect)    │  │
│  └──────────────────────────────────────────────────────────────┘  │
└────────────────────────────────┬────────────────────────────────────┘
                                 │
          ┌──────────────────────┼──────────────────────┐
          │                      │                      │
          ▼                      ▼                      ▼
┌─────────────────┐  ┌─────────────────┐  ┌─────────────────┐
│   Container 1   │  │   Container 2   │  │   Container 3   │
│   :8001/:9001   │  │   :8002/:9002   │  │   :8003/:9003   │
│                 │  │                 │  │                 │
│  WhatsApp API   │  │  WhatsApp API   │  │  WhatsApp API   │
│  (FastAPI)      │  │  (FastAPI)      │  │  (FastAPI)      │
└─────────────────┘  └─────────────────┘  └─────────────────┘
```

## Frontend Integration

### קבלת רשימת טלפונים פעילים

```javascript
// GET /api/routes
const response = await fetch('/api/routes');
const { phones } = await response.json();

// Response:
{
  "count": 2,
  "phones": [
    {
      "phoneId": "uuid-1",
      "phoneNumber": "+972501234567",
      "label": "Office",
      "status": "running",
      "routes": {
        "baseUrl": "/wa/972501234567",
        "byId": "/wa/id/uuid-1",
        "endpoints": {
          "status": "/wa/972501234567/status",
          "qrcode": "/wa/972501234567/qrcode",
          "qrcodeImage": "/wa/972501234567/qrcode/image",
          "sendText": "/wa/972501234567/send/text",
          "sendButtons": "/wa/972501234567/send/buttons",
          "sendList": "/wa/972501234567/send/list",
          "contacts": "/wa/972501234567/contacts",
          "authDashboard": "/wa/972501234567/auth/dashboard",
          "health": "/wa/972501234567/health"
        }
      }
    }
  ]
}
```

### שליחת הודעה

```javascript
// POST /wa/972501234567/send/text
await fetch('/wa/972501234567/send/text', {
  method: 'POST',
  headers: { 'Content-Type': 'application/json' },
  body: JSON.stringify({
    jid: '972509876543@s.whatsapp.net',
    text: 'שלום!'
  })
});
```

### הצגת QR Code

```html
<img src="/wa/972501234567/qrcode/image" alt="Scan QR" />
```

### בדיקת סטטוס חיבור

```javascript
// GET /wa/972501234567/status
const status = await fetch('/wa/972501234567/status').then(r => r.json());
// { "connected": true, "phone": "972501234567", ... }
```

## דרישות מקדימות

- .NET 8.0 SDK
- Docker
- Supabase account

## התקנה מהירה

### 1. Clone והגדרות

```bash
git clone <repo>
cd WhatsAppDockerManager

# העתק את קובץ ההגדרות
cp .env.example .env

# ערוך את ההגדרות
nano .env
```

### 2. עדכון Database

הרץ את ה-SQL ב-Supabase:

```sql
-- טבלת שרתים (Hosts)
CREATE TABLE IF NOT EXISTS hosts (
    id UUID PRIMARY KEY DEFAULT gen_random_uuid(),
    host_name VARCHAR(255) NOT NULL UNIQUE,
    ip_address VARCHAR(45) NOT NULL,
    external_ip VARCHAR(45),
    status VARCHAR(20) DEFAULT 'active',
    last_heartbeat TIMESTAMPTZ DEFAULT NOW(),
    max_containers INT DEFAULT 50,
    port_range_start INT DEFAULT 8001,
    port_range_end INT DEFAULT 8100,
    created_at TIMESTAMPTZ DEFAULT NOW(),
    updated_at TIMESTAMPTZ DEFAULT NOW()
);

-- הוספת שדות לטבלת phones
ALTER TABLE phones 
ADD COLUMN IF NOT EXISTS host_id UUID REFERENCES hosts(id),
ADD COLUMN IF NOT EXISTS container_id VARCHAR(100),
ADD COLUMN IF NOT EXISTS container_name VARCHAR(100),
ADD COLUMN IF NOT EXISTS api_port INT,
ADD COLUMN IF NOT EXISTS ws_port INT,
ADD COLUMN IF NOT EXISTS last_health_check TIMESTAMPTZ,
ADD COLUMN IF NOT EXISTS error_message TEXT;

-- טבלת אירועים
CREATE TABLE IF NOT EXISTS container_events (
    id UUID PRIMARY KEY DEFAULT gen_random_uuid(),
    phone_id UUID REFERENCES phones(id),
    host_id UUID REFERENCES hosts(id),
    event_type VARCHAR(50),
    event_data JSONB,
    created_at TIMESTAMPTZ DEFAULT NOW()
);

-- Indexes
CREATE INDEX IF NOT EXISTS idx_phones_host ON phones(host_id);
CREATE INDEX IF NOT EXISTS idx_phones_docker_status ON phones(docker_status);
CREATE INDEX IF NOT EXISTS idx_hosts_status ON hosts(status);
```

### 3. הרצה

#### Option A: עם Docker Compose (מומלץ לproduction)
```bash
docker-compose up -d
```

#### Option B: הרצה ישירה
```bash
cd src/WhatsAppDockerManager
dotnet run
```

#### Option C: Build ו-Run
```bash
dotnet build
dotnet run --project src/WhatsAppDockerManager
```

## קונפיגורציה

### appsettings.json

```json
{
  "AppSettings": {
    "Supabase": {
      "Url": "https://YOUR_PROJECT.supabase.co",
      "Key": "YOUR_SUPABASE_ANON_KEY"
    },
    "Docker": {
      "ImageName": "liorgr/whatsapp-single:latest",
      "DataBasePath": "/opt/whatsapp-data",
      "Timezone": "Asia/Jerusalem"
    },
    "Host": {
      "HostName": "host-1",
      "IpAddress": "0.0.0.0",
      "ExternalIp": "1.2.3.4",
      "PortRangeStart": 8001,
      "PortRangeEnd": 8100,
      "MaxContainers": 50,
      "HeartbeatIntervalSeconds": 30,
      "HealthCheckIntervalSeconds": 60
    },
    "Proxy": {
      "HttpPort": 5000,
      "TcpPortStart": 9001,
      "TcpPortEnd": 9100
    }
  }
}
```

### משתני סביבה

ניתן להגדיר גם דרך environment variables:

```bash
export AppSettings__Supabase__Url="https://..."
export AppSettings__Supabase__Key="..."
export AppSettings__Host__HostName="my-server"
```

## API Endpoints

### Host Management
```
GET  /api/host/status     # סטטוס השרת הנוכחי
GET  /api/host/all        # כל השרתים הפעילים
POST /api/host/sync       # טריגר סנכרון containers
GET  /api/host/health     # בדיקת בריאות
```

### Phone Management
```
GET  /api/phones                    # כל הטלפונים על השרת
GET  /api/phones/{id}               # מידע על טלפון ספציפי
POST /api/phones/{id}/start         # התחל container
POST /api/phones/{id}/stop          # עצור container
POST /api/phones/{id}/restart       # אתחל container
```

### Proxy Routes
```
/api/phone/{phoneNumber}/**   # Forward לcontainer לפי מספר טלפון
/api/id/{phoneId}/**          # Forward לcontainer לפי ID
```

### Webhooks
```
POST /api/webhook/container-status  # עדכון סטטוס מהcontainer
POST /api/webhook/host-register     # רישום שרת חדש
```

## ארכיטקטורה

```
┌─────────────────────────────────────────────────────────┐
│                    .NET Core Host                       │
├─────────────────────────────────────────────────────────┤
│  ┌─────────────┐  ┌─────────────┐  ┌─────────────────┐ │
│  │ HTTP Proxy  │  │  Container  │  │  Background     │ │
│  │   (YARP)    │  │  Manager    │  │  Services       │ │
│  └──────┬──────┘  └──────┬──────┘  └────────┬────────┘ │
│         │                │                   │          │
│  ┌──────┴──────┐  ┌──────┴──────┐  ┌────────┴────────┐ │
│  │ TCP Proxy   │  │   Docker    │  │  - Heartbeat    │ │
│  │ (WebSocket) │  │   Service   │  │  - HealthCheck  │ │
│  └─────────────┘  └─────────────┘  │  - Sync         │ │
│                                     └─────────────────┘ │
│                          │                              │
│                   ┌──────┴──────┐                       │
│                   │  Supabase   │                       │
│                   │   Client    │                       │
│                   └─────────────┘                       │
├─────────────────────────────────────────────────────────┤
│  Docker Containers                                      │
│  ┌──────────┐  ┌──────────┐  ┌──────────┐              │
│  │ WhatsApp │  │ WhatsApp │  │ WhatsApp │    ...       │
│  │  Phone 1 │  │  Phone 2 │  │  Phone 3 │              │
│  │  :8001   │  │  :8002   │  │  :8003   │              │
│  └──────────┘  └──────────┘  └──────────┘              │
└─────────────────────────────────────────────────────────┘
```

## High Availability

המערכת תומכת בריצה על מספר שרתים:

1. **Heartbeat** - כל שרת שולח heartbeat כל 30 שניות
2. **Dead Host Detection** - אם שרת לא שלח heartbeat 2 דקות, הוא נחשב "מת"
3. **Failover** - שרתים אחרים יקחו את הטלפונים של השרת המת

### הרצה על מספר שרתים

```bash
# Server 1
export AppSettings__Host__HostName="host-1"
export AppSettings__Host__ExternalIp="1.2.3.4"
dotnet run

# Server 2
export AppSettings__Host__HostName="host-2"
export AppSettings__Host__ExternalIp="5.6.7.8"
dotnet run
```

## Troubleshooting

### הcontainer לא עולה
```bash
# בדוק logs
docker logs whatsapp_<phone_number>

# בדוק סטטוס
curl http://localhost:5000/api/phones
```

### בעיות Docker socket
```bash
# Linux - ודא הרשאות
sudo chmod 666 /var/run/docker.sock

# או הוסף את המשתמש לקבוצת docker
sudo usermod -aG docker $USER
```

### בעיות חיבור ל-Supabase
```bash
# בדוק את ה-URL וה-Key
curl "https://YOUR_PROJECT.supabase.co/rest/v1/hosts" \
  -H "apikey: YOUR_KEY"
```

## פיתוח

```bash
# Development mode
cd src/WhatsAppDockerManager
dotnet watch run

# Run tests
dotnet test

# Build for production
dotnet publish -c Release -o ./publish
```

## רישיון

MIT
