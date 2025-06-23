# Build the operator
FROM mcr.microsoft.com/dotnet/sdk:9.0 as build
WORKDIR /operator

COPY ./ ./
RUN curl -L -o cfssl https://github.com/cloudflare/cfssl/releases/download/v1.5.0/cfssl_1.5.0_linux_amd64
RUN curl -L -o cfssljson https://github.com/cloudflare/cfssl/releases/download/v1.5.0/cfssljson_1.5.0_linux_amd64
RUN chmod +x ./cfssl
RUN chmod +x ./cfssljson
RUN mkdir out
RUN cp cfssl cfssljson out/
RUN dotnet publish -c Release -o out ./SentryOperator.csproj

# The runner for the application
FROM mcr.microsoft.com/dotnet/aspnet:9.0 as final

RUN addgroup k8s-operator && useradd -G k8s-operator operator-user

WORKDIR /operator
COPY --from=build /operator/out/ ./
RUN chown operator-user:k8s-operator -R .

USER operator-user

ENTRYPOINT [ "dotnet", "SentryOperator.dll" ]
