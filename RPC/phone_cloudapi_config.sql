
-- ── ב. המזהים של Cloud API פר טלפון ──────────────────────────────────────
-- נקרא רק במסלול cloudapi. ברגע ש-start_phone_prepare יחזיר את אותם
-- ארבעת השדות בתוך ה-JSON שלו, ContainerManager יפסיק לקרוא לכאן לבד.
create or replace function public.phone_cloudapi_config(p_phone_id uuid)
returns json
language sql
security definer
set search_path = public
as $$
  select json_build_object(
           'waba_id',            p.waba_id,
           'phone_number_id',    p.phone_number_id,
           'cloud_access_token', p.cloud_access_token,
           'cloud_verify_token', p.cloud_verify_token
         )
  from public.phones p
  where p.id = p_phone_id
    and coalesce(p.provider, 'baileys') = 'cloudapi';
$$;

comment on function public.phone_cloudapi_config(uuid) is
  'Cloud API identifiers for one phone. Returns null for baileys phones. '
  'Fallback until start_phone_prepare carries these fields itself.';

revoke all on function public.phone_cloudapi_config(uuid) from public, anon;


-- ── ג. לקיפול לתוך start_phone_prepare (אופציונלי, חוסך round trip) ──────
--
-- אם תרצה לוותר על ב' לגמרי: ב-start_phone_prepare, במקום שבו נבנה ה-JSON
-- המוחזר, הוסף את ארבעת המפתחות מאותה שורת phones שכבר נטענה שם:
--
--     'waba_id',            ph.waba_id,
--     'phone_number_id',    ph.phone_number_id,
--     'cloud_access_token', ph.cloud_access_token,
--     'cloud_verify_token', ph.cloud_verify_token
--
-- StartPrepareResult כבר מכיל את השדות האלה כ-nullable, ו-ContainerManager
-- מעדיף אותם על פני הקריאה הנפרדת — כך שזה מתחיל לעבוד בלי שינוי ב-C#.
