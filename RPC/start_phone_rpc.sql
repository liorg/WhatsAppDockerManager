
drop function if exists public.start_phone_prepare(uuid, uuid, text, text);

create or replace function public.start_phone_prepare(
    p_phone_id        uuid,
    p_host_id         uuid,
    p_status_starting text
)
returns jsonb
language plpgsql
security definer
set search_path = public
as $$
declare
    v_phone  phones%rowtype;
    v_fa     int[];
    v_ba     int[];
    v_email  text;
    v_masked text := '****anon';
    v_image  text;
begin
    -- revision: +1 אטומי על השורה עצמה (נעילת שורה)
    update phones
       set host_id           = coalesce(host_id, p_host_id),
           docker_status     = p_status_starting,
           auth_revision     = coalesce(auth_revision, 0) + 1,
           error_message     = null,
           last_health_check = now()
     where id = p_phone_id
    returning * into v_phone;

    if not found then
        raise exception 'phone % not found', p_phone_id;
    end if;

    -- image לפי provider
    select pr.image || ':' || coalesce(nullif(pr.tag, ''), 'latest')
      into v_image
      from providers pr
     where pr.id = coalesce(v_phone.provider, 'baileys')
       and pr.active;

    if v_image is null then
        raise exception 'provider % not found or inactive', v_phone.provider;
    end if;

    select coalesce(array_agg(api_port) filter (where api_port is not null), '{}'),
           coalesce(array_agg(ws_port)  filter (where ws_port  is not null), '{}')
      into v_fa, v_ba
      from phones
     where host_id = v_phone.host_id
       and id     <> p_phone_id
       and coalesce(container_id, '') <> '';   -- כל container קיים תופס פורט (גם disconnected/pending)

    if v_phone.user_id is not null then
        select email into v_email from auth.users where id = v_phone.user_id;
        if v_email like '%@%' then
            v_masked := rpad(left(split_part(v_email, '@', 1), 4), 4, '*')
                        || '**@**'
                        || coalesce(nullif(right(split_part(v_email, '@', 2), 1), ''), '*');
        else
            v_masked := '****@****';
        end if;
    end if;

    return jsonb_build_object(
        'host_id',        v_phone.host_id,
        'revision',       v_phone.auth_revision,
        'provider',       v_phone.provider,
        'image',          v_image,
        'masked_user',    v_masked,
        'used_api_ports', to_jsonb(v_fa),
        'used_ws_ports',  to_jsonb(v_ba)
    );
end;
$$;

-- ════════════════════════════════════════════════════════════════════
-- 3. start_phone_finish — עדכון סופי, רק אם ה-revision עדיין של ה-Start הזה.
--    מחזיר false אם Start חדש יותר כבר העלה את ה-revision (superseded).
-- ════════════════════════════════════════════════════════════════════
drop function if exists public.start_phone_finish(uuid, uuid, text, text, text, text, int, int, text, text, jsonb);
drop function if exists public.start_phone_finish(uuid, text, text, text, int, int, text, text);

create or replace function public.start_phone_finish(
    p_phone_id       uuid,
    p_revision       int,
    p_docker_status  text,
    p_container_id   text default null,
    p_container_name text default null,
    p_api_port       int  default null,
    p_ws_port        int  default null,
    p_docker_url     text default null,
    p_error          text default null
)
returns boolean
language plpgsql
security definer
set search_path = public
as $$
begin
    update phones
       set docker_status     = p_docker_status,
           container_id      = coalesce(p_container_id,   container_id),
           container_name    = coalesce(p_container_name, container_name),
           api_port          = coalesce(p_api_port,       api_port),
           ws_port           = coalesce(p_ws_port,        ws_port),
           docker_url        = coalesce(p_docker_url,     docker_url),
           error_message     = p_error,
           last_health_check = now()
     where id = p_phone_id
       and auth_revision = p_revision;
    return found;
end;
$$;

drop function if exists public.bump_phone_revision(uuid);

revoke all on function public.start_phone_prepare(uuid, uuid, text)                          from public, anon, authenticated;
revoke all on function public.start_phone_finish(uuid, int, text, text, text, int, int, text, text) from public, anon, authenticated;
grant execute on function public.start_phone_prepare(uuid, uuid, text)                          to service_role;
grant execute on function public.start_phone_finish(uuid, int, text, text, text, int, int, text, text) to service_role;

-- ════════════════════════════════════════════════════════════════════
-- 4. provider_images — כל ה-images הפעילים (ל-prepull)
-- ════════════════════════════════════════════════════════════════════
create or replace function public.provider_images()
returns jsonb
language sql
stable
security definer
set search_path = public
as $$
    select coalesce(jsonb_agg(distinct image || ':' || coalesce(nullif(tag, ''), 'latest')), '[]'::jsonb)
      from providers
     where active;
$$;

revoke all on function public.provider_images() from public, anon, authenticated;
grant execute on function public.provider_images() to service_role;