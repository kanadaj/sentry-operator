# sentry-operator
A Kubernetes operator written in .NET that uses the self-hosted docker-compose.yml as the ground truth

Images are under kanadaj/sentry-operator on docker.io.

See config/rbac and config/crds for the YAML files to install. I recommend using the kustomization yml to apply them since the default naming is rather bad.
