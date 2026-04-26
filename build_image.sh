-- גיבוי חוקי ה־Firewall הקיימים לפני ביצוע שינויים
 gcloud compute firewall-rules list --format=json > firewall-backup.json
/home/lior

--- יצירת Firewall Rule חדש שיאפשר גישה רק מכתובות ה־Tailscale
gcloud compute firewall-rules create allow-only-my-tailscale \
  --network=default \
  --allow=tcp:22,tcp:80,tcp:443,tcp:8000,tcp:5000 \
  --source-ranges=100.91.111.96/32,100.122.204.41/32 \
  --target-tags=tailscale-only

-- הוספת תג ל־VM כדי שיחול עליו חוק ה־Firewall החדש
gcloud compute instances add-tags instance-20260416-115407 \
  --tags=tailscale-only \
  --zone=europe-central2-c


 –- יצירת Static IP
gcloud compute addresses create my-static-ip \
  --region=europe-central2


  -- יצירת VM חדש מהתמונה
  gcloud compute instances create my-agent-host \
  --image=my-new-image \
  --zone=europe-central2-c \
  --machine-type=e2-medium \
  --boot-disk-size=20GB
   --address=my-static-ip


🔥 RESTORE לשרת קיים (להחליף דיסק)
1. כבה VM
gcloud compute instances stop instance-20260416-115407  
מחק דיסק
gcloud compute disks delete instance-20260416-115407
החלף בדיסק חדש שנוצר מהתמונה

gcloud compute disks create new-disk \
  --image=my-new-image \
  --zone=europe-central2-c

 חבר ל־VM
  gcloud compute instances attach-disk instance-20260416-115407 \
  --disk=new-disk \
  --boot


-- התחבר ל־VM דרך SSH
  ssh -i ~/.ssh/google_compute_engine lior@34.116.202.120

# צור את התיקייה בשרת
gcloud compute ssh instance-20260416-115407 \
  --zone=europe-central2-c \
  --tunnel-through-iap \
  --ssh-key-file ~/.ssh/gcp_cloud \
  --command="sudo mkdir -p /opt/myapp/releases/v1 && sudo chown -R lior:lior /opt/myapp"

-- העתק את הקבצים לשרת
   gcloud compute scp --recurse ./publish \
  lior@instance-20260416-115407:/opt/myapp/releases/v1 \
  --zone=europe-central2-c \
  --tunnel-through-iap \
  --ssh-key-file ~/.ssh/gcp_cloud
