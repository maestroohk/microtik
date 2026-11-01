#!/bin/sh
set -e

psql -v ON_ERROR_STOP=1 --username "$POSTGRES_USER" --dbname "$POSTGRES_DB" <<-EOSQL
	CREATE ROLE kibo_radius LOGIN PASSWORD '${KIBO_RADIUS_DB_PASSWORD}';
	CREATE DATABASE kibo_test OWNER kibo;
EOSQL
