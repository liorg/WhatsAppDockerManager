-- 013_optout_jid.sql — "may I send to this JID?" for the Manager's SendController
-- 972541112233@s.whatsapp.net → by number · 98765@lid → by the LIDs recorded at opt-out
create or replace function public.optout_is_blocked_jid(p_jid text)
returns boolean language sql stable security definer set search_path = public as $$
  with j as (
    select split_part(coalesce(p_jid, ''), '@', 1) as local,
           regexp_replace(split_part(coalesce(p_jid, ''), '@', 1), '\D', '', 'g') as num)
  select exists (
    select 1 from contact_optouts o, j
     where o.revoked_at is null
       and ((length(j.num) between 7 and 15 and o.number = j.num)
            or (j.local <> '' and j.local = any (o.lids))));
$$;
revoke all on function public.optout_is_blocked_jid(text) from public, anon;
grant execute on function public.optout_is_blocked_jid(text) to authenticated, service_role;
