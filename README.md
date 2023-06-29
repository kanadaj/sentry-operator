# sentry-operator
A Kubernetes operator written in .NET that uses the self-hosted docker-compose.yml as the ground truth

Images are under kanadaj/sentry-operator on docker.io.

See config/rbac and config/crds for the YAML files to install. I recommend using the kustomization yml to apply them since the default naming is rather bad.

Currently this operator requires cert-manager to be installed and a cluster-issuer called self-signed.

## TODO:
- [ ] Save the copies of the older versions of docker-compose into the project so we can avoid pulling them from GitHub every time
- [ ] Load local docker-compose.yml files if they exist instead of pulling from GitHub
- [ ] Add more settings to the CRD to customize deployment
- [ ] Add HPA support
- [ ] Check if certificate creating is still broken
- [ ] Only try installing the certificate if cert-manager is installed
- [X] Vroom vroom