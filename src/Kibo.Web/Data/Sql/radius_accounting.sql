CREATE OR REPLACE FUNCTION radius.combine_octets(p_octets bigint, p_gigawords bigint, p_previous bigint)
RETURNS bigint
LANGUAGE plpgsql IMMUTABLE AS $$
DECLARE
    v_total bigint := (COALESCE(p_gigawords, 0) << 32) + COALESCE(p_octets, 0);
    v_wraps bigint;
BEGIN
    IF COALESCE(p_gigawords, 0) = 0 AND p_previous IS NOT NULL AND p_previous >= 4294967296 THEN
        v_wraps := p_previous / 4294967296;
        IF (p_previous % 4294967296) > COALESCE(p_octets, 0) + 2147483648 THEN
            v_wraps := v_wraps + 1;
        END IF;
        v_total := v_wraps * 4294967296 + COALESCE(p_octets, 0);
    ELSIF COALESCE(p_gigawords, 0) = 0 AND p_previous IS NOT NULL AND p_previous > COALESCE(p_octets, 0) + 2147483648 THEN
        v_total := 4294967296 + COALESCE(p_octets, 0);
    END IF;
    RETURN v_total;
END;
$$;

CREATE OR REPLACE FUNCTION radius.account(
    p_status text,
    p_session_id text,
    p_unique_id text,
    p_username text,
    p_nas text,
    p_port text,
    p_session_time bigint,
    p_input_octets bigint,
    p_input_gigawords bigint,
    p_output_octets bigint,
    p_output_gigawords bigint,
    p_called text,
    p_calling text,
    p_cause text,
    p_framed_ip text,
    p_class text,
    p_event_epoch bigint)
RETURNS integer
LANGUAGE plpgsql SECURITY DEFINER SET search_path = pg_catalog, public, radius AS $$
DECLARE
    v_now timestamptz := now();
    v_status text := lower(p_status);
    v_tenant uuid;
    v_router uuid;
    v_subscription uuid;
    v_event_time timestamptz;
    v_session_time bigint := GREATEST(COALESCE(p_session_time, 0), 0);
    v_start timestamptz;
    v_in bigint;
    v_out bigint;
    r radius.radacct%ROWTYPE;
    v_found boolean := false;
    v_outcome text;
    v_id bigint;
    v_closed integer;
BEGIN
    SELECT n.tenant_id, n.router_id INTO v_tenant, v_router FROM radius.nas n WHERE n.nas_name = p_nas;
    IF p_event_epoch IS NOT NULL AND p_event_epoch > 0 THEN
        v_event_time := to_timestamp(p_event_epoch);
    END IF;

    IF v_status IN ('accounting-on', 'accounting-off') THEN
        UPDATE radius.radacct SET acctstoptime = v_now, acctterminatecause = 'NAS-Reboot'
        WHERE nasipaddress = p_nas AND acctstoptime IS NULL;
        GET DIAGNOSTICS v_closed = ROW_COUNT;
        INSERT INTO radius.acct_events (received_at, nas_ip_address, tenant_id, status_type, session_time, input_bytes,
            output_bytes, nas_event_time, outcome)
        VALUES (v_now, p_nas, v_tenant, v_status, 0, 0, 0, v_event_time, 'closed ' || v_closed || ' open sessions');
        RETURN 1;
    END IF;

    IF v_tenant IS NULL THEN
        INSERT INTO radius.acct_events (received_at, nas_ip_address, status_type, session_id, user_name, session_time,
            input_bytes, output_bytes, nas_event_time, class, outcome)
        VALUES (v_now, p_nas, v_status, p_session_id, p_username, v_session_time, 0, 0, v_event_time, p_class, 'rejected: unknown NAS');
        RETURN 1;
    END IF;

    PERFORM pg_advisory_xact_lock(hashtextextended(p_nas || '|' || COALESCE(p_session_id, '') || '|' || COALESCE(p_username, ''), 0));

    SELECT * INTO r FROM radius.radacct
    WHERE nasipaddress = p_nas AND acctsessionid = p_session_id AND username = p_username
    ORDER BY radacctid DESC LIMIT 1
    FOR UPDATE;
    v_found := FOUND;

    v_start := v_now - make_interval(secs => v_session_time);

    IF v_found THEN
        IF v_status = 'start' THEN
            IF r.acctstoptime IS NULL AND r.acctstarttime > v_now - interval '5 minutes' THEN
                v_outcome := 'duplicate start ignored';
            ELSIF r.acctstoptime IS NULL THEN
                UPDATE radius.radacct SET acctstoptime = COALESCE(acctupdatetime, acctstarttime), acctterminatecause = 'Session-Id-Reused'
                WHERE radacctid = r.radacctid;
                v_found := false;
                v_outcome := 'session id reused: previous session closed';
            ELSE
                v_found := false;
                v_outcome := 'new session with reused id';
            END IF;
        ELSIF v_session_time + 120 < COALESCE(r.acctsessiontime, 0)
              AND v_event_time IS NOT NULL AND r.nas_event_time IS NOT NULL AND v_event_time > r.nas_event_time THEN
            IF r.acctstoptime IS NULL THEN
                UPDATE radius.radacct SET acctstoptime = COALESCE(acctupdatetime, acctstarttime), acctterminatecause = 'Session-Id-Reused'
                WHERE radacctid = r.radacctid;
            END IF;
            v_found := false;
            v_outcome := 'session time went backwards: treated as a new session';
        ELSIF r.acctstoptime IS NOT NULL AND r.acctterminatecause NOT IN ('Stale-Session')
              AND abs(extract(epoch FROM (r.acctstarttime - v_start))) > 600 THEN
            v_found := false;
            v_outcome := 'closed session with same id: new session';
        END IF;
    END IF;

    v_in := radius.combine_octets(p_input_octets, p_input_gigawords, CASE WHEN v_found THEN r.acctinputoctets END);
    v_out := radius.combine_octets(p_output_octets, p_output_gigawords, CASE WHEN v_found THEN r.acctoutputoctets END);

    IF v_found THEN
        IF v_in < r.acctinputoctets OR v_out < r.acctoutputoctets THEN
            v_outcome := COALESCE(v_outcome, 'counter lower than stored (late or reordered packet): kept highest');
        END IF;

        UPDATE radius.radacct SET
            acctupdatetime = GREATEST(acctupdatetime, v_now),
            acctsessiontime = GREATEST(acctsessiontime, v_session_time),
            acctinputoctets = GREATEST(acctinputoctets, v_in),
            acctoutputoctets = GREATEST(acctoutputoctets, v_out),
            nas_event_time = GREATEST(nas_event_time, v_event_time),
            framedipaddress = COALESCE(NULLIF(p_framed_ip, ''), framedipaddress),
            update_count = update_count + 1,
            acctstoptime = CASE
                WHEN v_status = 'stop' THEN COALESCE(CASE WHEN acctterminatecause = 'Stale-Session' THEN NULL ELSE acctstoptime END, v_now)
                WHEN acctterminatecause = 'Stale-Session' THEN NULL
                ELSE acctstoptime END,
            acctterminatecause = CASE
                WHEN v_status = 'stop' THEN COALESCE(NULLIF(p_cause, ''), acctterminatecause)
                WHEN acctterminatecause = 'Stale-Session' THEN ''
                ELSE acctterminatecause END
        WHERE radacctid = r.radacctid;
        v_id := r.radacctid;
        v_outcome := COALESCE(v_outcome, CASE
            WHEN r.acctterminatecause = 'Stale-Session' THEN 'stale session resumed'
            WHEN v_status = 'stop' AND r.acctstoptime IS NOT NULL THEN 'duplicate stop'
            ELSE v_status || ' applied' END);
    ELSIF v_outcome IS DISTINCT FROM 'duplicate start ignored' THEN
        SELECT s.id INTO v_subscription
        FROM public.subscriptions s
        JOIN public.access_accounts a ON a.id = s.access_account_id
        WHERE s.id = CASE WHEN length(p_class) = 36 AND p_class ~ '^[0-9a-fA-F-]+$' THEN p_class::uuid END AND a.username = p_username AND a.tenant_id = v_tenant;

        IF v_subscription IS NULL THEN
            SELECT x.subscription_id INTO v_subscription FROM radius.access x
            WHERE x.tenant_id = v_tenant AND x.username = p_username;
        END IF;

        INSERT INTO radius.radacct (acctsessionid, acctuniqueid, username, nasipaddress, nasportid, acctstarttime,
            acctupdatetime, acctstoptime, acctsessiontime, acctinputoctets, acctoutputoctets, calledstationid,
            callingstationid, acctterminatecause, framedipaddress, class, tenant_id, router_id, subscription_id,
            nas_event_time, update_count)
        VALUES (p_session_id, p_unique_id, p_username, p_nas, p_port, CASE WHEN v_status = 'start' THEN v_now ELSE v_start END,
            v_now, CASE WHEN v_status = 'stop' THEN v_now END, v_session_time,
            CASE WHEN v_status = 'start' THEN 0 ELSE v_in END, CASE WHEN v_status = 'start' THEN 0 ELSE v_out END,
            p_called, p_calling, CASE WHEN v_status = 'stop' THEN COALESCE(p_cause, '') ELSE '' END,
            NULLIF(p_framed_ip, ''), NULLIF(p_class, ''), v_tenant, v_router, v_subscription, v_event_time, 1)
        RETURNING radacctid INTO v_id;
        v_outcome := COALESCE(v_outcome, CASE WHEN v_status = 'start' THEN 'start recorded' ELSE v_status || ' without start: session created' END);
    END IF;

    INSERT INTO radius.acct_events (received_at, nas_ip_address, tenant_id, status_type, session_id, user_name,
        session_time, input_bytes, output_bytes, nas_event_time, class, rad_acct_id, outcome)
    VALUES (v_now, p_nas, v_tenant, v_status, p_session_id, p_username, v_session_time, COALESCE(v_in, 0),
        COALESCE(v_out, 0), v_event_time, NULLIF(p_class, ''), v_id, v_outcome);
    RETURN 1;
END;
$$;
