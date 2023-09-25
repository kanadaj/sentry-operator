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
This operator does not install the following depenedencies:
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

## TODO:
- [X] ~~Vroom vroom~~
- [X] ~~Add more settings to the CRD to customize deployment~~
- [ ] Save the copies of the older versions of docker-compose into the project so we can avoid pulling them from GitHub every time
- [ ] Load local docker-compose.yml files if they exist instead of pulling from GitHub
- [X] ~~Add example config~~
- [ ] Add HPA support
- [X] ~~Check if certificate creating is still broken~~
- [ ] Only try installing the certificate if cert-manager is installed