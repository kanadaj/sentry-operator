# sentry-operator
A Kubernetes operator written in .NET that uses the self-hosted docker-compose.yml as the ground truth

Images are under kanadaj/sentry-operator on docker.io.

See [deploy.yaml](./deploy.yaml) for a deployment manifest. The CRD definition may not be up to date; see config/crds for the latest version generated automatically.

Currently this operator assumes that cert-manager is installed; however you can disable Certificate CRD generation by setting `.spec.certificate.install` to false.

## TODO:
- [ ] Save the copies of the older versions of docker-compose into the project so we can avoid pulling them from GitHub every time
- [ ] Load local docker-compose.yml files if they exist instead of pulling from GitHub
- [ ] Add more settings to the CRD to customize deployment
- [ ] Add HPA support
- [ ] Check if certificate creating is still broken
- [ ] Only try installing the certificate if cert-manager is installed
- [X] Vroom vroom