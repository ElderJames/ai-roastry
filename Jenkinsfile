
pipeline {
  agent {
    docker {
      image 'mcr.microsoft.com/dotnet/sdk:9.0'
      args '-u root'  // 以 root 用户运行
      reuseNode true
    }
  }
  environment {
    CONFIGURATION = "${env.BRANCH_NAME ==~ /master|release-.*/ ? 'Release' : 'Debug'}"
  }
  stages {
    stage('Remove unsupported dcproj') {
      // see https://github.com/dotnet/sdk/issues/35134
      steps {
        script {
          sh "dotnet sln remove \$(dotnet sln list | grep .dcproj)"
        }
      }
    }
    stage('Restore') {
      steps {
        script {
          sh "dotnet restore llm-pool.sln"
        }
      }
    }
    stage('Add dcproj back to sln') {
      // see https://github.com/dotnet/sdk/issues/35134
      steps {
        script {
          sh "dotnet sln add *.dcproj"
        }
      }
    }
    stage('Build') {
      steps {
        script {
          echo "${env.BRANCH_NAME}"
          sh "dotnet build --no-restore -c ${CONFIGURATION} llm-pool.sln"
        }
      }
    }
    stage('Test') {
      steps {
        script {
          sh "USE_INMEMORY_DB=true dotnet test --no-build -c ${CONFIGURATION} llm-pool.sln"
        }
      }
    }
    stage('Pack NuGet') {
      steps {
        script {
          if (CONFIGURATION == 'Release') {
            sh "dotnet pack src/LY.LlmPool.Client/LY.LlmPool.Client.csproj --no-build -c ${CONFIGURATION} --output ./nupkgs"
          }
          sh "dotnet pack src/LY.LlmPool.Client/LY.LlmPool.Client.csproj --no-build -c ${CONFIGURATION} --version-suffix ${env.BUILD_TAG} --output ./nupkgs"
        }
      }
      post {
        success {
          archiveArtifacts artifacts: 'nupkgs/*.nupkg', fingerprint: true
        }
      }
    }
    stage('Publish Nuget Packages') {
      // when {
      //   expression { return CONFIGURATION == 'Release' }
      // }
      steps {
        script {
          withCredentials([string(credentialsId: 'forgejo-jenkins-api-token', variable: 'NUGET_API_KEY')]) {
            sh 'dotnet nuget push nupkgs/*.nupkg --api-key $NUGET_API_KEY --source https://git.liany.com.cn/api/packages/lianyuan/nuget/index.json --skip-duplicate'
          }
        }
      }
    }
  }
  post {
    cleanup {
      cleanWs()
    }
  }
}
