DROP FUNCTION IF EXISTS radius.check_items(text, text);
DROP FUNCTION IF EXISTS radius.reply_items(text, text);
DROP FUNCTION IF EXISTS radius.evaluate(text, text);
DROP FUNCTION IF EXISTS radius.check_items(text, text, text);
DROP FUNCTION IF EXISTS radius.reply_items(text, text, text);
DROP FUNCTION IF EXISTS radius.evaluate(text, text, text);
DROP FUNCTION IF EXISTS radius.check_items(text, text, text, text);
DROP FUNCTION IF EXISTS radius.reply_items(text, text, text, text);
DROP FUNCTION IF EXISTS radius.evaluate(text, text, text, text);

CREATE OR REPLACE FUNCTION radius.rate_bytes_per_second(p_rate text)
RETURNS bigint
LANGUAGE plpgsql IMMUTABLE AS $$
DECLARE
    v_part text;
    v_total numeric := 0;
    v_number numeric;
    v_unit text;
BEGIN
    IF p_rate IS NULL OR p_rate = '' THEN
        RETURN 12500000;
    END IF;

    FOREACH v_part IN ARRAY string_to_array(split_part(p_rate, ' ', 1), '/') LOOP
        v_number := NULLIF(regexp_replace(v_part, '[^0-9.]', '', 'g'), '')::numeric;
        v_unit := lower(regexp_replace(v_part, '[0-9. ]', '', 'g'));
        IF v_number IS NULL OR v_number = 0 THEN
            v_total := v_total + 100000000;
        ELSE
            v_total := v_total + v_number * CASE v_unit WHEN 'k' THEN 1000 WHEN 'm' THEN 1000000 WHEN 'g' THEN 1000000000 ELSE 1 END;
        END IF;
    END LOOP;
    RETURN CEIL(v_total / 8)::bigint;
END;
$$;

CREATE OR REPLACE FUNCTION radius.subscription_usage(p_tenant uuid, p_username text, p_subscription uuid, p_counting_since timestamptz)
RETURNS TABLE (used_bytes bigint, used_seconds bigint, open_sessions integer, unreported_seconds bigint)
LANGUAGE sql STABLE AS $$
    SELECT COALESCE(SUM(COALESCE(r.acctinputoctets, 0) + COALESCE(r.acctoutputoctets, 0)), 0)::bigint,
           COALESCE(SUM(COALESCE(r.acctsessiontime, 0)), 0)::bigint,
           COUNT(*) FILTER (WHERE r.acctstoptime IS NULL)::integer,
           COALESCE(SUM(EXTRACT(EPOCH FROM (now() - COALESCE(r.acctupdatetime, r.acctstarttime))))
               FILTER (WHERE r.acctstoptime IS NULL), 0)::bigint
    FROM radius.radacct r
    WHERE r.tenant_id = p_tenant
      AND ((p_subscription IS NOT NULL AND r.subscription_id = p_subscription)
        OR (r.subscription_id IS NULL AND r.username = p_username
            AND r.acctstarttime >= COALESCE(p_counting_since, '-infinity'::timestamptz)));
$$;

CREATE OR REPLACE FUNCTION radius.pending_logins(p_tenant uuid, p_username text)
RETURNS integer
LANGUAGE sql STABLE AS $$
    SELECT COUNT(*)::integer
    FROM radius.radpostauth p
    JOIN radius.nas n ON n.nas_name = p.nasipaddress AND n.tenant_id = p_tenant
    WHERE p.username = p_username
      AND p.reply = 'Access-Accept'
      AND p.authdate > now() - interval '60 seconds'
      AND NOT EXISTS (
          SELECT 1 FROM radius.radacct r
          WHERE r.tenant_id = p_tenant AND r.username = p.username AND r.nasipaddress = p.nasipaddress
            AND COALESCE(r.callingstationid, '') = COALESCE(p.callingstationid, '')
            AND r.acctstarttime >= p.authdate - interval '10 seconds');
$$;

CREATE OR REPLACE FUNCTION radius.simultaneous_sessions(p_username text, p_nas text)
RETURNS integer
LANGUAGE sql STABLE AS $$
    SELECT (
        SELECT COUNT(*) FROM radius.radacct a
        WHERE a.tenant_id = n.tenant_id AND a.username = p_username AND a.acctstoptime IS NULL
          AND COALESCE(a.acctupdatetime, a.acctstarttime) > now() - interval '5 minutes')::integer
        + radius.pending_logins(n.tenant_id, p_username)
    FROM radius.nas n WHERE n.nas_name = p_nas;
$$;

CREATE OR REPLACE FUNCTION radius.evaluate(p_username text, p_nas text, p_service text DEFAULT '', p_calling text DEFAULT '')
RETURNS TABLE (
    allowed boolean,
    message text,
    password text,
    simultaneous_use integer,
    session_timeout bigint,
    rate_limit text,
    total_limit bigint,
    framed_ip text,
    subscription_id uuid,
    interim_seconds integer)
LANGUAGE plpgsql STABLE AS $$
DECLARE
    v_tenant uuid;
    a radius.access%ROWTYPE;
    v_now timestamptz := now();
    v_used_bytes bigint := 0;
    v_used_seconds bigint := 0;
    v_rate text;
    v_total bigint;
    v_timeout numeric;
    v_local time;
    v_inside boolean;
    v_window_left numeric;
    v_open integer := 0;
    v_unreported bigint := 0;
    v_reserve bigint := 0;
    v_remaining bigint;
BEGIN
    SELECT n.tenant_id INTO v_tenant FROM radius.nas n WHERE n.nas_name = p_nas;
    IF v_tenant IS NULL THEN
        RETURN;
    END IF;

    IF p_calling <> '' AND (
        SELECT COUNT(*) FROM radius.radpostauth p
        WHERE p.nasipaddress = p_nas AND p.callingstationid = p_calling AND p.reply = 'Access-Reject'
          AND p.authdate > now() - interval '10 minutes') >= 10 THEN
        RETURN QUERY SELECT false, 'Too many failed attempts from this device. Try again in 10 minutes.', NULL::text, 1,
            NULL::bigint, NULL::text, NULL::bigint, NULL::text, NULL::uuid, NULL::integer;
        RETURN;
    END IF;

    SELECT * INTO a FROM radius.access x WHERE x.tenant_id = v_tenant AND x.username = p_username;
    IF NOT FOUND THEN
        RETURN;
    END IF;

    IF NOT EXISTS (SELECT 1 FROM public.tenants t WHERE t.id = v_tenant AND t.status = 0) THEN
        RETURN QUERY SELECT false, 'This hotspot is temporarily unavailable.', a.password, 1, NULL::bigint,
            NULL::text, NULL::bigint, NULL::text, NULL::uuid, NULL::integer;
        RETURN;
    END IF;

    IF (a.service_type = 'ppp' AND p_service = 'Login-User') OR (a.service_type = 'hotspot' AND p_service = 'Framed-User') THEN
        RETURN QUERY SELECT false, 'This login is not valid for this service.', a.password, 1, NULL::bigint,
            NULL::text, NULL::bigint, NULL::text, NULL::uuid, NULL::integer;
        RETURN;
    END IF;

    IF NOT a.enabled THEN
        RETURN QUERY SELECT false, 'You have no active package. Buy a package to continue.', a.password, 1, NULL::bigint,
            NULL::text, NULL::bigint, NULL::text, NULL::uuid, NULL::integer;
        RETURN;
    END IF;

    IF a.expires_at IS NOT NULL AND a.expires_at <= v_now THEN
        RETURN QUERY SELECT false, 'Your package has expired.', a.password, 1, NULL::bigint,
            NULL::text, NULL::bigint, NULL::text, a.subscription_id, NULL::integer;
        RETURN;
    END IF;

    IF a.expires_at IS NULL AND a.activate_by IS NOT NULL AND a.activate_by <= v_now THEN
        RETURN QUERY SELECT false, 'This package was not used before its deadline.', a.password, 1, NULL::bigint,
            NULL::text, NULL::bigint, NULL::text, a.subscription_id, NULL::integer;
        RETURN;
    END IF;

    SELECT u.used_bytes, u.used_seconds, u.open_sessions, u.unreported_seconds
    INTO v_used_bytes, v_used_seconds, v_open, v_unreported
    FROM radius.subscription_usage(v_tenant, p_username, a.subscription_id, a.counting_since) u;
    v_open := v_open + radius.pending_logins(v_tenant, p_username);
    v_reserve := v_unreported * radius.rate_bytes_per_second(a.rate_limit);

    IF a.time_limit_seconds IS NOT NULL AND v_used_seconds >= a.time_limit_seconds THEN
        RETURN QUERY SELECT false, 'Your online time allowance is used up.', a.password, 1, NULL::bigint,
            NULL::text, NULL::bigint, NULL::text, a.subscription_id, NULL::integer;
        RETURN;
    END IF;

    v_rate := a.rate_limit;
    IF a.data_limit_bytes IS NOT NULL THEN
        IF v_used_bytes >= a.data_limit_bytes THEN
            IF a.throttled_rate_limit IS NULL THEN
                RETURN QUERY SELECT false, 'Your data allowance is used up.', a.password, 1, NULL::bigint,
                    NULL::text, NULL::bigint, NULL::text, a.subscription_id, NULL::integer;
                RETURN;
            END IF;
            v_rate := a.throttled_rate_limit;
            v_total := NULL;
        ELSIF a.throttled_rate_limit IS NULL OR a.fair_use_router_cutoff THEN
            v_remaining := a.data_limit_bytes - v_used_bytes - v_reserve;
            IF v_remaining <= 0 THEN
                RETURN QUERY SELECT false, 'Your other devices are using the last of your data allowance.', a.password, 1,
                    NULL::bigint, NULL::text, NULL::bigint, NULL::text, a.subscription_id, NULL::integer;
                RETURN;
            END IF;
            v_total := CASE WHEN v_open > 0 THEN GREATEST(1, v_remaining / (v_open + 1)) ELSE v_remaining END;
        END IF;
    END IF;

    IF a.window_start IS NOT NULL AND a.window_end IS NOT NULL THEN
        v_local := (v_now AT TIME ZONE a.time_zone)::time;
        IF a.window_start < a.window_end THEN
            v_inside := v_local >= a.window_start AND v_local < a.window_end;
        ELSE
            v_inside := v_local >= a.window_start OR v_local < a.window_end;
        END IF;

        IF NOT v_inside THEN
            RETURN QUERY SELECT false,
                'This package works between ' || to_char(a.window_start, 'HH24:MI') || ' and ' || to_char(a.window_end, 'HH24:MI') || '.',
                a.password, 1, NULL::bigint, NULL::text, NULL::bigint, NULL::text, a.subscription_id, NULL::integer;
            RETURN;
        END IF;

        v_window_left := EXTRACT(EPOCH FROM (a.window_end - v_local));
        IF v_window_left <= 0 THEN
            v_window_left := v_window_left + 86400;
        END IF;
    END IF;

    v_timeout := NULL;
    IF a.expires_at IS NOT NULL THEN
        v_timeout := EXTRACT(EPOCH FROM (a.expires_at - v_now));
    ELSIF a.validity_seconds IS NOT NULL THEN
        v_timeout := a.validity_seconds;
    END IF;

    IF a.time_limit_seconds IS NOT NULL THEN
        v_timeout := LEAST(COALESCE(v_timeout, a.time_limit_seconds - v_used_seconds), a.time_limit_seconds - v_used_seconds);
    END IF;

    IF a.max_session_seconds IS NOT NULL THEN
        v_timeout := LEAST(COALESCE(v_timeout, a.max_session_seconds), a.max_session_seconds);
    END IF;

    IF v_window_left IS NOT NULL THEN
        v_timeout := LEAST(COALESCE(v_timeout, v_window_left), v_window_left);
    END IF;

    RETURN QUERY SELECT true, NULL::text, a.password, a.simultaneous_use,
        CASE WHEN v_timeout IS NULL THEN NULL ELSE GREATEST(1, CEIL(v_timeout))::bigint END,
        NULLIF(v_rate, ''), v_total, a.framed_ip_address, a.subscription_id, a.accounting_interval_seconds;
END;
$$;

CREATE OR REPLACE FUNCTION radius.check_items(p_username text, p_nas text, p_service text DEFAULT '', p_calling text DEFAULT '')
RETURNS TABLE (id integer, username text, attribute text, value text, op text)
LANGUAGE sql STABLE AS $$
    WITH e AS (SELECT * FROM radius.evaluate(p_username, p_nas, p_service, p_calling))
    SELECT 1, p_username, 'Cleartext-Password', e.password, ':=' FROM e WHERE e.allowed
    UNION ALL
    SELECT 2, p_username, 'Simultaneous-Use', e.simultaneous_use::text, ':=' FROM e WHERE e.allowed
    UNION ALL
    SELECT 3, p_username, 'Auth-Type', 'Reject', ':=' FROM e WHERE NOT e.allowed;
$$;

CREATE OR REPLACE FUNCTION radius.reply_items(p_username text, p_nas text, p_service text DEFAULT '', p_calling text DEFAULT '')
RETURNS TABLE (id integer, username text, attribute text, value text, op text)
LANGUAGE sql STABLE AS $$
    WITH e AS (SELECT * FROM radius.evaluate(p_username, p_nas, p_service, p_calling))
    SELECT 1, p_username, 'Reply-Message', e.message, ':=' FROM e WHERE NOT e.allowed
    UNION ALL
    SELECT 2, p_username, 'Session-Timeout', e.session_timeout::text, ':=' FROM e WHERE e.allowed AND e.session_timeout IS NOT NULL
    UNION ALL
    SELECT 3, p_username, 'Mikrotik-Rate-Limit', e.rate_limit, ':=' FROM e WHERE e.allowed AND e.rate_limit IS NOT NULL
    UNION ALL
    SELECT 4, p_username, 'Mikrotik-Total-Limit', (e.total_limit % 4294967296)::text, ':=' FROM e WHERE e.allowed AND e.total_limit IS NOT NULL
    UNION ALL
    SELECT 5, p_username, 'Mikrotik-Total-Limit-Gigawords', (e.total_limit / 4294967296)::text, ':=' FROM e WHERE e.allowed AND e.total_limit IS NOT NULL
    UNION ALL
    SELECT 6, p_username, 'Acct-Interim-Interval', COALESCE(e.interim_seconds, 60)::text, ':=' FROM e WHERE e.allowed
    UNION ALL
    SELECT 7, p_username, 'Class', e.subscription_id::text, ':=' FROM e WHERE e.allowed AND e.subscription_id IS NOT NULL
    UNION ALL
    SELECT 8, p_username, 'Framed-IP-Address', e.framed_ip, ':=' FROM e WHERE e.allowed AND e.framed_ip IS NOT NULL;
$$;

CREATE OR REPLACE FUNCTION prevent_ledger_mutation() RETURNS trigger
LANGUAGE plpgsql AS $$
BEGIN
    RAISE EXCEPTION 'ledger_entries is append-only; record a correcting entry instead';
END;
$$;

DROP TRIGGER IF EXISTS ledger_entries_append_only ON ledger_entries;
CREATE TRIGGER ledger_entries_append_only
    BEFORE UPDATE OR DELETE ON ledger_entries
    FOR EACH ROW EXECUTE FUNCTION prevent_ledger_mutation();

DO $$
BEGIN
    IF EXISTS (SELECT 1 FROM pg_roles WHERE rolname = 'kibo_radius') THEN
        GRANT USAGE ON SCHEMA radius TO kibo_radius;
        GRANT SELECT ON radius.access, radius.nas TO kibo_radius;
        GRANT SELECT (id, status) ON public.tenants TO kibo_radius;
        GRANT SELECT, INSERT, UPDATE ON radius.radacct, radius.radpostauth TO kibo_radius;
        GRANT USAGE ON ALL SEQUENCES IN SCHEMA radius TO kibo_radius;
        GRANT EXECUTE ON ALL FUNCTIONS IN SCHEMA radius TO kibo_radius;
        REVOKE INSERT, UPDATE ON radius.radacct FROM kibo_radius;
        GRANT SELECT ON radius.radacct TO kibo_radius;
    END IF;
END;
$$;
