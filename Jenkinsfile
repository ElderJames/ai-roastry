
pipeline {
  agent {
    docker {
      image 'mcr.microsoft.com/dotnet/sdk:9.0'
      args '-u root'  // 以 root 用户运行
      reuseNode true
    }
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
          sh "dotnet build --no-restore llm-pool.sln"
        }
      }
    }
    stage('Test') {
      steps {
        script {
          sh "dotnet test --no-build llm-pool.sln"
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
