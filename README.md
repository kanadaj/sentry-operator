# sentry-operator
A Kubernetes operator written in .NET that uses the self-hosted docker-compose.yml as the ground truth

Images are under `kanadaj/sentry-operator` on [dockerhub](https://hub.docker.com/r/kanadaj/sentry-operator).

See [deploy.yaml](./deploy.yaml) for a deployment manifest. The CRD definition may not be up to date; see [config/crds](./config/crds) for the latest version generated automatically.

Currently this operator assumes that cert-manager is installed; however you can disable Certificate CRD generation by setting `.spec.certificate.install` to false.

For a full list of supported settings, see [SentryDeployment.cs](./Entities/SentryDeployment.cs).

# Getting started
1. Deploy ClickHouse with an operator or via Helm: `helm install -n sentry --set shards=1 --set repliaCount=1 --set zookeeper.enabled=false --set persistence.size=200Gi --set image.digest=21.8 clickhouse bitnami/clickhouse` (see https://artifacthub.io/packages/helm/bitnami/clickhouse for documentation). Note that this operator was not tested against replicated clickhouse and documentation on setting it up correctly is sparse.
2. Deploy postgres with an operator or via Helm: `helm install -n sentry --set primary.persistence.size=10Gi postgres bitnami/postgresql` (see https://artifacthub.io/packages/helm/bitnami/postgresql for documentation)
3. Deploy Redis with an operator or via Helm: `helm install -n sentry redis bitnami/redis` (see https://artifacthub.io/packages/helm/bitnami/redis for documentation)
4. Deploy Kafka with an operator or via Helm: `helm install -n sentry kafka bitnami/kafka` (see https://artifacthub.io/packages/helm/bitnami/kafka for documentation)
5. Deploy the operator: `kubectl apply -f deploy.yaml`
6. Create a SentryDeployment CRD: `kubectl apply -f example.yaml`

Please be aware that for steps 1-4, you need to adjust the settings for your needs, particularly the storage settings.

# Example config

See [example.yaml](./example.yaml) for an example configuration.

# Configuration
The operator creates a `sentry-config` secret in the namespace. While this secret can be configured in the operator for the most part, some advanced config is not available via the operator.

If you wish to use these (for example for VSTS support or to use my S3 nodestore implementation) 
you need to remove the owner reference from the secret and edit it manually. 
Once there is no owner reference present, the operator will not touch the secret.

The operator also creates a snuba-env ConfigMap and a sentry-env Secret. These are not managed by the operator after creation and can be used to set environment variables for Snuba and Sentry containers respectively.

# Resource limits
The operator will set resource limits on all containers except the web service. You can override these limits (including for the web service) by creating elements under resources matching the name of the service.

Additionally, the following special cases exist which are applied as wildcards to all services with the name containing the same:
- attachments-consumer
- consumer
- ingest
- forwarder
- replacer
- geoip

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
attachments-consumer-6fd6d8fc7-78rbq                            1/1     Running   0             2d23h
billing-metrics-consumer-5ddb8d87c9-6hq8n                       1/1     Running   0             23h
clickhouse-954d8d97c-z75st                                      1/1     Running   0             3d1h
cron-6c4db7577-gkdm7                                            1/1     Running   0             2d17h
events-consumer-6b7f5c9d56-6mxhd                                1/1     Running   0             23h
generic-metrics-consumer-7858977975-9v85m                       1/1     Running   0             2d17h
geoipupdate-5b966f7fc8-zxlb2                                    1/1     Running   0             23h
ingest-monitors-6786b5b6d4-mjrjc                                1/1     Running   0             23h
ingest-occurrences-645785c69d-fckfw                             1/1     Running   0             23h
ingest-profiles-685db99db9-pz9jv                                1/1     Running   0             23h
ingest-replay-recordings-54cd8b5574-xfcf2                       1/1     Running   0             23h
kafka-0                                                         1/1     Running   0             13d
kafka-ui-56cf9d4cf7-l9qzq                                       1/1     Running   0             26d
kafka-zookeeper-0                                               1/1     Running   0             14d
memcached-594b9f749b-wptcj                                      1/1     Running   0             2d23h
metrics-consumer-dcb77b79c-dsd6s                                1/1     Running   0             2d17h
post-process-forwarder-errors-69c64d9896-tw29n                  1/1     Running   0             23h
post-process-forwarder-issue-platform-677cfdd48c-rtnhp          1/1     Running   0             23h
post-process-forwarder-transactions-55c458ccb7-sp6hw            1/1     Running   0             2d17h
postgres-6568588789-92q7l                                       1/1     Running   0             38d
redis-7fc84c67b-4r4kf                                           1/1     Running   0             13d
relay-7c9ff59748-dlslp                                          1/1     Running   0             2d23h
sentry-cleanup-84978d8c67-65vs5                                 1/1     Running   0             2d23h
sentry-operator-c6474b6c9-rjm46                                 1/1     Running   0             23h
snuba-api-568bd6585f-vvg88                                      1/1     Running   0             2d17h
snuba-errors-consumer-867d8598cf-9xlp4                          1/1     Running   0             23h
snuba-generic-metrics-counters-consumer-548f8ccc55-78plh        1/1     Running   0             23h
snuba-generic-metrics-distributions-consumer-85748b679c-dvh5d   1/1     Running   0             23h
snuba-generic-metrics-sets-consumer-76f84dfc55-sm5x9            1/1     Running   0             23h
snuba-issue-occurrence-consumer-568b8d577-m9gln                 1/1     Running   0             23h
snuba-metrics-consumer-74c9b896bb-xtstk                         1/1     Running   0             23h
snuba-outcomes-consumer-686c9b8f88-x4s2q                        1/1     Running   0             23h
snuba-profiling-functions-consumer-5679b6948-sdtxq              1/1     Running   0             23h
snuba-profiling-profiles-consumer-75c85b5f85-6xxxt              1/1     Running   0             23h
snuba-replacer-9cf49c5f6-zxwrn                                  1/1     Running   0             23h
snuba-replays-consumer-7464db4dc5-4zhz8                         1/1     Running   0             23h
snuba-spans-consumer-68675d779d-gbk96                           1/1     Running   0             23h
snuba-subscription-consumer-events-865f9d6689-7j2jp             1/1     Running   0             23h
snuba-subscription-consumer-metrics-56774cbcc5-brkqg            1/1     Running   0             23h
snuba-subscription-consumer-transactions-7f85f768c-n5ct2        1/1     Running   0             23h
snuba-transactions-consumer-5549cdd96d-r5rgj                    1/1     Running   0             2d17h
subscription-consumer-events-79cf6f6cfd-v8n8m                   1/1     Running   0             23h
subscription-consumer-generic-metrics-64977d94c6-66n2g          1/1     Running   0             23h
subscription-consumer-metrics-5746bf6d-csn25                    1/1     Running   0             23h
subscription-consumer-transactions-79c9f66558-2w5fn             1/1     Running   0             23h
symbolicator-7c746b9bb7-gr87f                                   1/1     Running   0             23h
symbolicator-cleanup-5847847f5c-m8dz5                           1/1     Running   0             2d23h
transactions-consumer-69d57fb8fd-ftw4d                          1/1     Running   0             2d17h
vroom-79f8fc649-d5bbz                                           1/1     Running   0             2d23h
vroom-cleanup-594d484b67-nkhzb                                  1/1     Running   0             2d23h
web-6889fbbf77-s5rx5                                            1/1     Running   0             2d23h
worker-868c86bfb4-dc6tf                                         1/1     Running   0             24h
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