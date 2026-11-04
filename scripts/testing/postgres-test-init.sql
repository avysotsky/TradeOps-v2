-- Only the separate TradeOps test container mounts this initialization script.
COMMENT ON DATABASE postgres IS 'tradeops-test-server:v1';
REVOKE ALL ON DATABASE postgres FROM PUBLIC;
