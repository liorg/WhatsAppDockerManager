
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
  where p.id = p_phone_id;
$$;
 
grant execute on function public.phone_cloudapi_config(uuid) to anon, authenticated, service_role;
 
 
