# sentry-operator
A Kubernetes operator written in .NET that uses the self-hosted docker-compose.yml as the ground truth

Images are under `kanadaj/sentry-operator` on [dockerhub](https://hub.docker.com/r/kanadaj/sentry-operator).

See [deploy.yaml](./deploy.yaml) for a deployment manifest. The CRD definition may not be up to date; see [config/crds](./config/crds) for the latest version generated automatically.

Currently this operator assumes that cert-manager is installed; however you can disable Certificate CRD generation by setting `.spec.certificate.install` to false.

For a full list of supported settings, see [SentryDeployment.cs](./Entities/SentryDeployment.cs).

# Example config

```yaml
apiVersion: sentry.io/v1
kind: SentryDeployment
metadata:
  name: sentry
  namespace: sentry
spec:
  environment:
    OPENAI_API_KEY: ''
    SENTRY_EVENT_RETENTION_DAYS: '30'
    SENTRY_MAIL_HOST: ''
    REDIS_PORT: '6379'
    CLICKHOUSE_PORT: '9000'
    SENTRY_MAX_EXTERNAL_SOURCEMAP_SIZE: ''
    GEOIPUPDATE_ACCOUNT_ID: ''
    GEOIPUPDATE_LICENSE_KEY: ''
    GEOIPUPDATE_EDITION_IDS: 'GeoLite2-City'
  version: 23.6.1
```

# Dependencies
This operator does **_not_** install the following depenedencies:
- Redis
- Clickhouse
- Postgres
- Kafka
- Zookeeper

Since these are all stateful services, it is recommended that you create these manually or use a dedicated operator to manage them.

Sentry will look for the following Services and ports:
- `redis:6379`
- `clickhouse:9000`
- `postgres:5432`
- `kafka:9092`

# Why?
Over the past few years Sentry has grown enormous. The current self-hosted docker-compose file requires _50_ Docker containers (+1 for the SMTP):

```tsv
NAME                                                            READY   STATUS      RESTARTS       AGE
attachments-consumer-ff9f5d7c5-zd2v6                            1/1     Running     0              24m
billing-metrics-consumer-78bbb8585b-c5wnl                       1/1     Running     0              24m
clickhouse-678c6f5d56-6l2xf                                     1/1     Running     0              115d
cron-794bfbb7cb-2p7fw                                           1/1     Running     0              24m
events-consumer-84797b96df-wdp92                                1/1     Running     0              24m
generic-metrics-consumer-596c9d7654-djcds                       1/1     Running     0              24m
geoipupdate-8445c6dc49-tgs5k                                    1/1     Running     0              36m
ingest-occurrences-9d4ff7c57-zx5nt                              1/1     Running     0              24m
ingest-profiles-7df7686cb-xh9b7                                 1/1     Running     0              24m
ingest-replay-recordings-6ddb497d65-29b6l                       1/1     Running     0              24m
kafka-0                                                         1/1     Running     1 (104d ago)   104d
kafka-zookeeper-0                                               1/1     Running     0              612d
memcached-859644c6cc-f6kz7                                      1/1     Running     0              12m
metrics-consumer-865b59bb6b-zscb9                               1/1     Running     0              24m
post-process-forwarder-errors-5558b5f5c9-5jpsw                  1/1     Running     0              24m
post-process-forwarder-issue-platform-95b8d9c46-kz2nt           1/1     Running     0              24m
post-process-forwarder-transactions-9546459dd-n7hj9             1/1     Running     0              24m
postgres-5bcbb7f7c7-cw9tw                                       1/1     Running     0              115d
redis-7d68c457df-mz98t                                          1/1     Running     0              7h58m
relay-7cf7fdd8d8-fgl9h                                          1/1     Running     0              24m
sentry-cleanup-5474d67b56-j8vqp                                 1/1     Running     0              24m
sentry-operator-66b7f76fd6-gk9q7                                1/1     Running     0              12m
snuba-api-5865f695c7-9krml                                      1/1     Running     0              16m
snuba-consumer-b845cfccb-4bpgk                                  1/1     Running     0              31m
snuba-generic-metrics-counters-consumer-8b645bd45-wk7rc         1/1     Running     0              31m
snuba-generic-metrics-distributions-consumer-689977d9b9-hg6f4   1/1     Running     0              31m
snuba-generic-metrics-sets-consumer-546575d869-jzrzn            1/1     Running     0              31m
snuba-issue-occurrence-consumer-56b7dc459-wqddn                 1/1     Running     0              31m
snuba-metrics-consumer-95ddbcf54-sxqhh                          1/1     Running     0              31m
snuba-outcomes-consumer-7f6987c68c-kz657                        1/1     Running     0              31m
snuba-profiling-functions-consumer-59458b56d5-sm7k5             1/1     Running     0              31m
snuba-profiling-profiles-consumer-c8bb66448-h8ckm               1/1     Running     0              31m
snuba-replacer-856469c6b-zxnmg                                  1/1     Running     0              31m
snuba-replays-consumer-65fc4b88f6-5vgj5                         1/1     Running     0              31m
snuba-sessions-consumer-5784988d69-6jlgc                        1/1     Running     0              31m
snuba-subscription-consumer-events-6bc94f94b9-dhv5f             1/1     Running     0              31m
snuba-subscription-consumer-metrics-65b4597db5-qlcxg            1/1     Running     0              31m
snuba-subscription-consumer-sessions-8f95456d7-s2q6m            1/1     Running     0              31m
snuba-subscription-consumer-transactions-7789b57845-vj5zm       1/1     Running     0              31m
snuba-transactions-consumer-76c646d7cc-s7hnj                    1/1     Running     1 (20m ago)    31m
subscription-consumer-events-7875fff649-cqkrx                   1/1     Running     0              24m
subscription-consumer-generic-metrics-bbb868467-xr2rv           1/1     Running     0              24m
subscription-consumer-metrics-776b88cf9f-mrwvh                  1/1     Running     0              24m
subscription-consumer-transactions-76f8544997-f5pw4             1/1     Running     0              24m
symbolicator-7c7cd5b77f-td2zd                                   1/1     Running     0              36m
symbolicator-cleanup-77d48f488d-6k6r5                           1/1     Running     0              36m
transactions-consumer-659dcc97d5-pvtdm                          1/1     Running     0              24m
vroom-5c8c65d494-t92v5                                          1/1     Running     0              24m
vroom-cleanup-54b6cf54f-r7zbm                                   1/1     Running     0              24m
web-7b865dcb88-pnk72                                            1/1     Running     0              16m
worker-7ff67c8fd8-5nrrg                                         1/1     Running     0              24m
```

On a single node this is easy enough to install using docker-compose (although not very scalable), but on a 
Kubernetes cluster this is a bit more difficult. There are a few Helm charts available, 
but they are all out of date (typically made for Sentry 10) and don't support the latest version of Sentry.

This operator aims to solve that problem by using the self-hosted docker-compose file to figure out what 
containers to install. This means that the operator will always be up to date with the latest version of Sentry.

If a new version of sentry introduces new configuration that has to be made, it's easy enough to 
accommodate it by using the existing converters in `/Docker/Converters` to manipulate the resulting Pods,
or by adding a new converter.

## TODO:
- [X] ~~Vroom vroom~~
- [X] ~~Add more settings to the CRD to customize deployment~~
- [ ] Save the copies of the older versions of docker-compose into the project so we can avoid pulling them from GitHub every time
- [ ] Load local docker-compose.yml files if they exist instead of pulling from GitHub
- [X] ~~Add example config~~
- [ ] Add HPA support
- [X] ~~Check if certificate creating is still broken~~
- [ ] Only try installing the certificate if cert-manager is installed