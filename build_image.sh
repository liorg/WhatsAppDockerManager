sudo shutdown -h now

exit


gcloud compute images create my-image \
  --source-disk=instance-20260416-115407 \
  --source-disk-zone=europe-central2-c


  gcloud compute images create  my-agent-host \
  --source-disk=instance-20260416-115407 \
  --source-disk-zone=europe-central2-c


  gcloud compute instances create my-agent-host \
  --image=my-new-image \
  --zone=europe-central2-c \
  --machine-type=e2-medium \
  --boot-disk-size=20GB


🔥 RESTORE לשרת קיים (להחליף דיסק)
1. כבה VM
gcloud compute instances stop instance-20260416-115407  
מחק דיסק
gcloud compute disks delete instance-20260416-115407
החלף בדיסק חדש שנוצר מהתמונה

gcloud compute disks create new-disk \
  --image=my-new-image \
  --zone=europe-central2-c

4. חבר ל־VM
  gcloud compute instances attach-disk instance-20260416-115407 \
  --disk=new-disk \
  --boot



  ssh -i ~/.ssh/google_compute_engine lior@34.116.202.120
