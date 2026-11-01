CREATE OR REPLACE FUNCTION radius.evaluate(p_username text, p_nas text)
RETURNS TABLE (
    allowed boolean,
    message text,
    password text,
    simultaneous_use integer,
    session_timeout bigint,
    rate_limit text,
    total_limit bigint,
    framed_ip text,
    subscription_id uuid)
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
BEGIN
    SELECT n.tenant_id INTO v_tenant FROM radius.nas n WHERE n.nas_name = p_nas;
    IF v_tenant IS NULL THEN
        RETURN;
    END IF;

    SELECT * INTO a FROM radius.access x WHERE x.tenant_id = v_tenant AND x.username = p_username;
    IF NOT FOUND THEN
        RETURN;
    END IF;

    IF NOT EXISTS (SELECT 1 FROM public.tenants t WHERE t.id = v_tenant AND t.status = 0) THEN
        RETURN QUERY SELECT false, 'This hotspot is temporarily unavailable.', a.password, 1, NULL::bigint,
            NULL::text, NULL::bigint, NULL::text, NULL::uuid;
        RETURN;
    END IF;

    IF NOT a.enabled THEN
        RETURN QUERY SELECT false, 'You have no active package. Buy a package to continue.', a.password, 1, NULL::bigint,
            NULL::text, NULL::bigint, NULL::text, NULL::uuid;
        RETURN;
    END IF;

    IF a.expires_at IS NOT NULL AND a.expires_at <= v_now THEN
        RETURN QUERY SELECT false, 'Your package has expired.', a.password, 1, NULL::bigint,
            NULL::text, NULL::bigint, NULL::text, a.subscription_id;
        RETURN;
    END IF;

    IF a.expires_at IS NULL AND a.activate_by IS NOT NULL AND a.activate_by <= v_now THEN
        RETURN QUERY SELECT false, 'This package was not used before its deadline.', a.password, 1, NULL::bigint,
            NULL::text, NULL::bigint, NULL::text, a.subscription_id;
        RETURN;
    END IF;

    SELECT COALESCE(SUM(COALESCE(r.acctinputoctets, 0) + COALESCE(r.acctoutputoctets, 0)), 0),
           COALESCE(SUM(COALESCE(r.acctsessiontime, 0)), 0)
    INTO v_used_bytes, v_used_seconds
    FROM radius.radacct r
    JOIN radius.nas n ON n.nas_name = r.nasipaddress
    WHERE n.tenant_id = v_tenant
      AND r.username = p_username
      AND r.acctstarttime >= COALESCE(a.counting_since, '-infinity'::timestamptz);

    IF a.time_limit_seconds IS NOT NULL AND v_used_seconds >= a.time_limit_seconds THEN
        RETURN QUERY SELECT false, 'Your online time allowance is used up.', a.password, 1, NULL::bigint,
            NULL::text, NULL::bigint, NULL::text, a.subscription_id;
        RETURN;
    END IF;

    v_rate := a.rate_limit;
    IF a.data_limit_bytes IS NOT NULL THEN
        IF v_used_bytes >= a.data_limit_bytes THEN
            IF a.throttled_rate_limit IS NULL THEN
                RETURN QUERY SELECT false, 'Your data allowance is used up.', a.password, 1, NULL::bigint,
                    NULL::text, NULL::bigint, NULL::text, a.subscription_id;
                RETURN;
            END IF;
            v_rate := a.throttled_rate_limit;
            v_total := NULL;
        ELSE
            v_total := a.data_limit_bytes - v_used_bytes;
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
                a.password, 1, NULL::bigint, NULL::text, NULL::bigint, NULL::text, a.subscription_id;
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
        NULLIF(v_rate, ''), v_total, a.framed_ip_address, a.subscription_id;
END;
$$;

CREATE OR REPLACE FUNCTION radius.check_items(p_username text, p_nas text)
RETURNS TABLE (id integer, username text, attribute text, value text, op text)
LANGUAGE sql STABLE AS $$
    WITH e AS (SELECT * FROM radius.evaluate(p_username, p_nas))
    SELECT 1, p_username, 'Cleartext-Password', e.password, ':=' FROM e WHERE e.allowed
    UNION ALL
    SELECT 2, p_username, 'Simultaneous-Use', e.simultaneous_use::text, ':=' FROM e WHERE e.allowed
    UNION ALL
    SELECT 3, p_username, 'Auth-Type', 'Reject', ':=' FROM e WHERE NOT e.allowed;
$$;

CREATE OR REPLACE FUNCTION radius.reply_items(p_username text, p_nas text)
RETURNS TABLE (id integer, username text, attribute text, value text, op text)
LANGUAGE sql STABLE AS $$
    WITH e AS (SELECT * FROM radius.evaluate(p_username, p_nas))
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
    SELECT 6, p_username, 'Acct-Interim-Interval', '60', ':=' FROM e WHERE e.allowed
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
    END IF;
END;
$$;
